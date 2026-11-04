using System.Text;
using ChaosNet.Scheduling;
using ChaosNet.Tracing;

namespace ChaosNet.Runtime;

/// <summary>A bug detected during an iteration.</summary>
internal sealed record BugInfo(BugKind Kind, string Message, Exception? Exception, int Step, int? OperationId);

/// <summary>The outcome of one controlled execution.</summary>
internal sealed record IterationResult(
    int Steps,
    IReadOnlyList<Decision> Decisions,
    BugInfo? Bug,
    bool HitStepBound,
    IReadOnlyList<OperationInfo> Operations,
    IReadOnlyList<TimelineStep>? Timeline,
    IReadOnlyList<string> Warnings,
    TimeSpan VirtualTime);

/// <summary>
/// The controlled runtime for a single iteration. It owns the operations, runs their work items one at a time on
/// the calling thread, asks the strategy which operation runs at every scheduling point, records every decision,
/// and detects assertion failures, unhandled exceptions, deadlocks, livelocks and liveness violations.
/// </summary>
internal sealed class ChaosRuntime
{
    [ThreadStatic]
    private static ChaosRuntime? t_current;

    // Flows with the ExecutionContext into anything the test starts, including uncontrolled thread-pool work. This
    // lets the APIs tell "production mode" (no test running) from "escaped the scheduler" (a test is running, but
    // on another thread).
    private static readonly AsyncLocal<ChaosRuntime?> s_flow = new();
    private static readonly Action<Task, object?> s_onOperationCompleted = OnOperationCompleted;
    private static long s_epochs;
    private static readonly TimeSpan UncontrolledProbe = TimeSpan.FromMilliseconds(200);

    private readonly TestConfig _config;
    private readonly ISchedulingStrategy _strategy;
    private readonly int _maxUnfairSteps;
    private readonly List<Operation> _operations = [];
    private readonly List<SchedulableOperation> _enabled = [];
    private readonly List<Decision> _decisions = [];
    private readonly List<WaitRecord> _waits = [];
    private readonly List<ChaosMonitor> _monitors = [];
    private readonly Dictionary<Type, ChaosMonitor> _monitorsByType = [];
    private readonly Lock _inboxGate = new();
    private readonly List<(Operation Operation, Task Work)> _inbox = [];
    private readonly List<string> _warnings = [];
    private readonly List<TimelineStep>? _timeline;
    private List<string>? _stepEvents;
    private Xoshiro256 _fairRandom;
    private volatile bool _inboxPending;
    private volatile bool _active;
    private int _threadId;
    private Operation? _root;
    private Operation? _timerOperation;
    private Operation? _running;
    private int _currentId = -2;
    private int _step;
    private bool _hitStepBound;
    private BugInfo? _bug;

    public ChaosRuntime(TestConfig config, ISchedulingStrategy strategy, bool record)
    {
        _config = config;
        _strategy = strategy;
        _maxUnfairSteps = config.EffectiveMaxUnfairSteps;
        _timeline = record ? [] : null;
        _fairRandom = new Xoshiro256((strategy.CurrentIterationSeed ?? 0x5EED5EED5EED5EEDUL) ^ 0xFA1F_FA1F_FA1F_FA1FUL);
    }

    /// <summary>Gets a process-unique id of this iteration. Primitives use it to reset per-iteration state.</summary>
    public long Epoch { get; } = Interlocked.Increment(ref s_epochs);

    public VirtualClock Clock { get; } = new();

    public int Step => _step;

    public bool IsRecording => _timeline is not null;

    public bool IsSchedulerThread => Environment.CurrentManagedThreadId == _threadId;

    /// <summary>Gets the operation whose work item is executing right now.</summary>
    public Operation CurrentOperation => _running ?? throw new InvalidOperationException("No controlled operation is running.");

    /// <summary>Gets the runtime controlling the current thread, without uncontrolled-thread checks.</summary>
    public static ChaosRuntime? Current => t_current is { _active: true } rt ? rt : null;

    /// <summary>
    /// Gets the runtime for an API call. Returns <see langword="null"/> in production mode (no test running) and
    /// reports a bug and throws when a test is running but the call comes from a thread the scheduler does not control.
    /// </summary>
    public static ChaosRuntime? ForApi()
    {
        ChaosRuntime? rt = t_current;
        if (rt is { _active: true })
        {
            return rt;
        }

        ChaosRuntime? flowed = s_flow.Value;
        if (flowed is { _active: true })
        {
            string message =
                $"A Chaos.Net API was called from uncontrolled thread {Environment.CurrentManagedThreadId} " +
                $"({Thread.CurrentThread.Name ?? (Thread.CurrentThread.IsThreadPoolThread ? "thread pool" : "unnamed")}) while a controlled test is running. " +
                "The code escaped the scheduler, typically through Task.Run, ConfigureAwait(false), a raw Thread or Timer, or Task.Delay. " +
                "Use ControlledTask.Run / ControlledTask.Delay / Chaos.TimeProvider and drop ConfigureAwait(false) in code under test.";
            flowed.RecordBug(BugKind.UncontrolledConcurrency, message, null);
            throw new UncontrolledConcurrencyException(message);
        }

        return null;
    }

    public bool IsRunning(Operation operation) => ReferenceEquals(_running, operation);

    /// <summary>Runs one iteration to completion on the calling thread.</summary>
    public IterationResult Run(Func<Task> test)
    {
        _threadId = Environment.CurrentManagedThreadId;
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        ChaosRuntime? previous = t_current;
        t_current = this;
        s_flow.Value = this;
        _active = true;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            _root = CreateOperation("root", isSystem: false);
            StartOperation(_root, test);
            Loop();
        }
        catch (NondeterminismException e)
        {
            RecordBug(BugKind.Nondeterminism, e.Message, null);
        }
        finally
        {
            _active = false;
            t_current = previous;
            s_flow.Value = null;
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        var operations = _operations
            .Select(o => new OperationInfo(o.Id, o.Label, o.CreatedAtStep, o.CompletedAtStep))
            .ToList();
        return new IterationResult(_step, _decisions, _bug, _hitStepBound, operations, _timeline, _warnings, Clock.Elapsed);
    }

    private void Loop()
    {
        while (_bug is null)
        {
            if (_inboxPending)
            {
                DrainInbox();
            }

            if (_step >= _config.MaxSteps)
            {
                OnStepBoundReached();
                return;
            }

            CollectEnabled();
            if (_enabled.Count == 0)
            {
                if (_config.UncontrolledGracePeriod > TimeSpan.Zero && WaitForInbox(_config.UncontrolledGracePeriod))
                {
                    continue;
                }

                // About to report a deadlock in which nothing waits on a controlled primitive: that is the signature
                // of a test awaiting uncontrolled work. Probe briefly so the report says what really happened.
                if (!_root!.IsCompleted && _waits.Count == 0 && WaitForInbox(UncontrolledProbe))
                {
                    continue;
                }

                OnQuiescent();
                return;
            }

            int chosen = ChooseOperation();
            _step++;
            if (chosen == SchedulableOperation.ClockId)
            {
                FireClock();
            }
            else
            {
                RunStep(_operations[chosen]);
            }

            if (_config.LivenessTemperatureThreshold > 0)
            {
                CheckTemperature();
            }
        }
    }

    private void CollectEnabled()
    {
        _enabled.Clear();
        foreach (Operation op in _operations)
        {
            if (op.Queue.Count > 0)
            {
                _enabled.Add(new SchedulableOperation(op.Id, op.Label));
            }
        }

        // Timers stop firing once the test body has completed; remaining enabled work is drained to quiescence.
        if (!_root!.IsCompleted && Clock.HasPendingTimers &&
            (_config.TimerPolicy == TimerPolicy.Nondeterministic || _enabled.Count == 0))
        {
            _enabled.Add(new SchedulableOperation(SchedulableOperation.ClockId, "clock"));
        }
    }

    private int ChooseOperation()
    {
        int id = !_strategy.IsFair && _step >= _maxUnfairSteps
            ? _enabled[_fairRandom.Next(_enabled.Count)].Id
            : _strategy.NextOperation(_enabled, _currentId, _step);

        bool valid = false;
        foreach (SchedulableOperation e in _enabled)
        {
            if (e.Id == id)
            {
                valid = true;
                break;
            }
        }

        if (!valid)
        {
            throw new InvalidOperationException($"Strategy '{_strategy.Name}' chose operation {id}, which is not enabled.");
        }

        _decisions.Add(new Decision(DecisionKind.Schedule, id));
        return id;
    }

    private void RunStep(Operation op)
    {
        Task work = op.Queue.Dequeue();
        _running = op;
        _currentId = op.Id;
        BeginStepRecord(op.Id);
        SynchronizationContext.SetSynchronizationContext(op.Context);
        try
        {
            op.Scheduler.Execute(work);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(null);
            _running = null;
        }
    }

    private void FireClock()
    {
        _timerOperation ??= CreateOperation("timer callbacks", isSystem: true);
        Operation op = _timerOperation;
        _running = op;
        _currentId = SchedulableOperation.ClockId;
        BeginStepRecord(SchedulableOperation.ClockId);
        VirtualTimer timer = Clock.PopNext();
        if (IsRecording)
        {
            Note($"t={Clock.Elapsed.TotalMilliseconds:0.###}ms fire {timer.Name}");
        }

        SynchronizationContext.SetSynchronizationContext(op.Context);
        try
        {
            timer.Fire();
        }
        catch (Exception e) when (e is not (ChaosAssertionException or UncontrolledConcurrencyException or NondeterminismException))
        {
            RecordBug(BugKind.UnhandledException, $"Timer callback '{timer.Name}' threw {e.GetType().Name}: {e.Message}", e);
        }
        catch (Exception)
        {
            // Already recorded by the API that threw.
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(null);
            _running = null;
        }
    }

    private void OnQuiescent()
    {
        bool resourceWait = _waits.Exists(w => w.Kind == BlockKind.Resource);
        if (!_root!.IsCompleted || (_config.ReportBlockedOperationsAsDeadlock && resourceWait))
        {
            RecordBug(BugKind.Deadlock, DescribeDeadlock(), null);
            return;
        }

        foreach (ChaosMonitor m in _monitors)
        {
            if (m.IsHot)
            {
                RecordBug(BugKind.LivenessViolation,
                    $"Liveness monitor '{m.Name}' is still hot ({m.HotReason}) at the end of the execution (hot since step {m.HotSinceStep}).", null);
                return;
            }
        }
    }

    private void OnStepBoundReached()
    {
        _hitStepBound = true;
        foreach (ChaosMonitor m in _monitors)
        {
            if (m.IsHot)
            {
                RecordBug(BugKind.LivenessViolation,
                    $"Liveness monitor '{m.Name}' is hot ({m.HotReason}) when the step bound of {_config.MaxSteps} was reached (hot since step {m.HotSinceStep}).", null);
                return;
            }
        }

        if (_config.ReportLivelocks && !_root!.IsCompleted)
        {
            CollectEnabled();
            string enabled = string.Join(", ", _enabled.Select(e => e.IsClock ? "clock" : _operations[e.Id].ToString()));
            RecordBug(BugKind.Livelock,
                $"The test did not terminate within {_config.MaxSteps} scheduling steps although operations kept running (enabled: {enabled}). " +
                "This is a livelock or an unbounded loop. If the system is meant to run forever, set ReportLivelocks = false and express progress with a ChaosMonitor.", null);
        }
    }

    private void CheckTemperature()
    {
        foreach (ChaosMonitor m in _monitors)
        {
            if (m.IsHot && _step - m.HotSinceStep > _config.LivenessTemperatureThreshold)
            {
                RecordBug(BugKind.LivenessViolation,
                    $"Liveness monitor '{m.Name}' has been hot ({m.HotReason}) for more than {_config.LivenessTemperatureThreshold} steps (since step {m.HotSinceStep}).", null);
                return;
            }
        }
    }

    private string DescribeDeadlock()
    {
        var sb = new StringBuilder();
        sb.Append("Deadlock: no operation can make progress");
        sb.Append(_root!.IsCompleted ? " (the test body completed, but operations are stuck on resources)." : " and the test body has not completed.");
        bool controlledWaits = false;
        foreach (Operation op in _operations)
        {
            if (op.IsSystem)
            {
                continue;
            }

            var waits = _waits.Where(w => w.Operation == op).ToList();
            foreach (WaitRecord w in waits)
            {
                controlledWaits = true;
                sb.AppendLine().Append("  ").Append(op).Append(" is blocked on ").Append(w.Description);
            }

            if (waits.Count == 0 && !op.IsCompleted)
            {
                sb.AppendLine().Append("  ").Append(op).Append(" is waiting for a task that never completes");
            }
        }

        if (!controlledWaits)
        {
            sb.AppendLine().Append(
                "  (No operation is blocked on a Chaos.Net primitive. If the test awaits uncontrolled work such as real I/O, Task.Delay or Task.Run, " +
                "the scheduler cannot see it; use controlled equivalents or set UncontrolledGracePeriod.)");
        }

        return sb.ToString();
    }

    // ---- operations ---------------------------------------------------------------------------------------------

    private Operation CreateOperation(string label, bool isSystem)
    {
        var op = new Operation(this, _operations.Count, label, _step, isSystem);
        _operations.Add(op);
        return op;
    }

    public Task Spawn(Func<Task> body, string? label)
    {
        Operation op = CreateOperation(label ?? $"task{_operations.Count}", isSystem: false);
        if (IsRecording)
        {
            Note($"spawn {op}");
        }

        return StartOperation(op, body);
    }

    public Task<T> Spawn<T>(Func<Task<T>> body, string? label)
    {
        Operation op = CreateOperation(label ?? $"task{_operations.Count}", isSystem: false);
        if (IsRecording)
        {
            Note($"spawn {op}");
        }

        Task<T> task = Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.DenyChildAttach, op.Scheduler).Unwrap();
        task.ContinueWith(s_onOperationCompleted, op, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private static Task StartOperation(Operation op, Func<Task> body)
    {
        Task task = Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.DenyChildAttach, op.Scheduler).Unwrap();
        task.ContinueWith(s_onOperationCompleted, op, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private static void OnOperationCompleted(Task task, object? state)
    {
        var op = (Operation)state!;
        ChaosRuntime rt = op.Runtime;
        if (!rt._active)
        {
            return;
        }

        if (!rt.IsSchedulerThread)
        {
            rt.OnUncontrolledWork(op, "completed its task");
        }

        op.IsCompleted = true;
        op.CompletedAtStep = rt._step;
        if (rt.IsRecording)
        {
            rt.Note($"{op} {(task.IsFaulted ? "faulted" : task.IsCanceled ? "canceled" : "completed")}");
        }

        if (task.IsFaulted)
        {
            Exception ex = task.Exception!.InnerExceptions.Count == 1 ? task.Exception.InnerException! : task.Exception;
            if (ex is ChaosAssertionException or UncontrolledConcurrencyException or NondeterminismException)
            {
                // Recorded when it was thrown; this only matters if the original bug was somehow cleared.
                rt.RecordBug(BugKind.AssertionFailure, ex.Message, ex);
            }
            else if (op == rt._root || (rt._config.FailOnUnhandledExceptionsInOperations && ex is not OperationCanceledException))
            {
                rt.RecordBug(BugKind.UnhandledException, $"Unhandled {ex.GetType().Name} in {op}: {ex.Message}", ex);
            }
        }
        else if (task.IsCanceled && op == rt._root)
        {
            rt.RecordBug(BugKind.UnhandledException, "The test body was canceled.", null);
        }
    }

    internal void OnCallbackException(Operation op, Exception e)
    {
        if (e is ChaosAssertionException or UncontrolledConcurrencyException or NondeterminismException)
        {
            return;
        }

        RecordBug(BugKind.UnhandledException, $"Unhandled {e.GetType().Name} in an async void method or posted callback of {op}: {e.Message}", e);
    }

    // ---- work queues --------------------------------------------------------------------------------------------

    /// <summary>Queues a work item to an operation. Thread-safe: work from foreign threads goes through the inbox.</summary>
    public void Enqueue(Operation op, Task work)
    {
        if (IsSchedulerThread)
        {
            if (_active)
            {
                op.Queue.Enqueue(work);
            }

            return;
        }

        if (!_active)
        {
            return;
        }

        lock (_inboxGate)
        {
            _inbox.Add((op, work));
            _inboxPending = true;
        }

        OnUncontrolledWork(op, "received a continuation");
    }

    private void OnUncontrolledWork(Operation op, string what)
    {
        string message =
            $"{op} {what} on uncontrolled thread {Environment.CurrentManagedThreadId}" +
            $"{(Thread.CurrentThread.IsThreadPoolThread ? " (thread pool)" : string.Empty)}. Work escaped the Chaos.Net scheduler " +
            "(Task.Run, Task.Delay, ConfigureAwait(false), raw threads/timers, real I/O, or RunContinuationsAsynchronously feeding Task.WhenAll), " +
            "so this execution is not deterministic.";
        if (_config.UncontrolledConcurrency == UncontrolledConcurrencyPolicy.Fail)
        {
            RecordBug(BugKind.UncontrolledConcurrency, message, null);
        }
        else
        {
            lock (_inboxGate)
            {
                if (_warnings.Count < 20)
                {
                    _warnings.Add(message);
                }
            }
        }
    }

    private void DrainInbox()
    {
        lock (_inboxGate)
        {
            foreach ((Operation op, Task work) in _inbox)
            {
                op.Queue.Enqueue(work);
            }

            _inbox.Clear();
            _inboxPending = false;
        }
    }

    private bool WaitForInbox(TimeSpan grace)
    {
        var deadline = DateTime.UtcNow + grace;
        while (DateTime.UtcNow < deadline)
        {
            if (_inboxPending)
            {
                DrainInbox();
                return true;
            }

            Thread.Sleep(1);
        }

        return false;
    }

    /// <summary>Queues <paramref name="continuation"/> to the running operation: an explicit scheduling point.</summary>
    public void YieldCurrent(Action continuation) => OperationSynchronizationContext.PostAction(CurrentOperation, continuation);

    // ---- waits, notes, choices, bugs ----------------------------------------------------------------------------

    public WaitRecord BeginWait(string description, BlockKind kind)
    {
        var record = new WaitRecord(CurrentOperation, description, kind);
        _waits.Add(record);
        if (IsRecording)
        {
            Note($"blocked on {description}");
        }

        return record;
    }

    public void EndWait(WaitRecord record) => _waits.Remove(record);

    public void Note(string text) => _stepEvents?.Add(text);

    private void BeginStepRecord(int operationId)
    {
        if (_timeline is not null)
        {
            _stepEvents = [];
            _timeline.Add(new TimelineStep(_step, operationId, _stepEvents));
        }
    }

    public bool NextBoolean()
    {
        bool value;
        try
        {
            value = !_strategy.IsFair && _step >= _maxUnfairSteps ? _fairRandom.NextBoolean() : _strategy.NextBoolean(_step);
        }
        catch (NondeterminismException e)
        {
            RecordBug(BugKind.Nondeterminism, e.Message, null);
            throw;
        }

        _decisions.Add(new Decision(DecisionKind.Boolean, value ? 1 : 0));
        if (IsRecording)
        {
            Note($"RandomBool() = {(value ? "true" : "false")}");
        }

        return value;
    }

    public int NextInteger(int maxValue)
    {
        int value;
        try
        {
            value = !_strategy.IsFair && _step >= _maxUnfairSteps ? _fairRandom.Next(maxValue) : _strategy.NextInteger(maxValue, _step);
        }
        catch (NondeterminismException e)
        {
            RecordBug(BugKind.Nondeterminism, e.Message, null);
            throw;
        }

        if ((uint)value >= (uint)maxValue)
        {
            throw new InvalidOperationException($"Strategy '{_strategy.Name}' returned {value} for RandomInt({maxValue}).");
        }

        _decisions.Add(new Decision(DecisionKind.Integer, value));
        if (IsRecording)
        {
            Note($"RandomInt({maxValue}) = {value}");
        }

        return value;
    }

    public void RecordBug(BugKind kind, string message, Exception? exception)
    {
        var bug = new BugInfo(kind, message, exception, _step, _running?.Id);
        if (Interlocked.CompareExchange(ref _bug, bug, null) is null && IsRecording && IsSchedulerThread)
        {
            Note($"BUG ({kind}): {FirstLine(message)}");
        }
    }

    private static string FirstLine(string s)
    {
        int i = s.IndexOf('\n', StringComparison.Ordinal);
        return i < 0 ? s : s[..i].TrimEnd();
    }

    // ---- monitors -----------------------------------------------------------------------------------------------

    public T GetMonitor<T>()
        where T : ChaosMonitor, new()
    {
        if (!_monitorsByType.TryGetValue(typeof(T), out ChaosMonitor? monitor))
        {
            monitor = RegisterMonitor(new T());
            _monitorsByType[typeof(T)] = monitor;
        }

        return (T)monitor;
    }

    public T RegisterMonitor<T>(T monitor)
        where T : ChaosMonitor
    {
        if (monitor.Runtime != this)
        {
            monitor.Attach(this);
            _monitors.Add(monitor);
        }

        return monitor;
    }
}
