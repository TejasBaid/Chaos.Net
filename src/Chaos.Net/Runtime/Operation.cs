namespace ChaosNet.Runtime;

/// <summary>Why an operation is waiting. Only <see cref="Resource"/> waits make a quiescent state a deadlock after the test body finished.</summary>
internal enum BlockKind
{
    /// <summary>Waiting to acquire a lock, a semaphore, or space in a bounded channel.</summary>
    Resource,

    /// <summary>Waiting to receive from a channel (idle server loops are normal).</summary>
    Receive,
}

/// <summary>A registered wait on a controlled primitive, used for deadlock diagnosis.</summary>
internal sealed class WaitRecord(Operation operation, string description, BlockKind kind)
{
    public Operation Operation { get; } = operation;

    public string Description { get; } = description;

    public BlockKind Kind { get; } = kind;
}

/// <summary>
/// A controlled operation: a logical thread of asynchronous execution (the test body, or one
/// <see cref="ControlledTask.Run(Func{Task}, string?)"/>). Each operation owns a synchronization context and a task
/// scheduler; every continuation captured inside the operation is queued here as a work item instead of running on
/// the thread pool. The scheduler runs one work item at a time, so each work item is the atomic unit between two
/// scheduling points.
/// </summary>
internal sealed class Operation
{
    public Operation(ChaosRuntime runtime, int id, string label, int createdAtStep, bool isSystem)
    {
        Runtime = runtime;
        Id = id;
        Label = label;
        CreatedAtStep = createdAtStep;
        IsSystem = isSystem;
        Context = new OperationSynchronizationContext(this);
        Scheduler = new OperationTaskScheduler(this);
    }

    public ChaosRuntime Runtime { get; }

    public int Id { get; }

    public string Label { get; }

    public int CreatedAtStep { get; }

    public int? CompletedAtStep { get; set; }

    /// <summary>System operations (the timer-callback operation) never count as blocked.</summary>
    public bool IsSystem { get; }

    public volatile bool IsCompleted;

    /// <summary>Pending work items (FIFO). The operation is enabled iff this is non-empty.</summary>
    public Queue<Task> Queue { get; } = new();

    public OperationSynchronizationContext Context { get; }

    public OperationTaskScheduler Scheduler { get; }

    public override string ToString() => $"op{Id} '{Label}'";
}

/// <summary>Routes <c>await</c> continuations (and <c>Task.Yield</c>) captured inside an operation back into that operation's queue.</summary>
internal sealed class OperationSynchronizationContext(Operation operation) : SynchronizationContext
{
    private static readonly Action<object?> s_invoke = Invoke;

    public Operation Operation { get; } = operation;

    public override void Post(SendOrPostCallback d, object? state)
    {
        var work = new Task(s_invoke, new PostedCallback(Operation, d, state));
        work.Start(Operation.Scheduler);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Operation.Runtime.IsSchedulerThread)
        {
            d(state);
            return;
        }

        throw new NotSupportedException("Synchronous Send to a controlled operation from an uncontrolled thread is not supported.");
    }

    public override SynchronizationContext CreateCopy() => this;

    internal static void PostAction(Operation operation, Action action)
    {
        var work = new Task(s_invoke, new PostedCallback(operation, static s => ((Action)s!)(), action));
        work.Start(operation.Scheduler);
    }

    private static void Invoke(object? state)
    {
        var posted = (PostedCallback)state!;
        try
        {
            posted.Callback(posted.State);
        }
        catch (Exception e)
        {
            posted.Operation.Runtime.OnCallbackException(posted.Operation, e);
        }
    }

    private sealed record PostedCallback(Operation Operation, SendOrPostCallback Callback, object? State);
}

/// <summary>
/// Queues tasks scheduled on an operation (its first work item, <c>Task.Factory.StartNew</c> and <c>ContinueWith</c>
/// using <see cref="TaskScheduler.Current"/>) into the operation's queue.
/// </summary>
internal sealed class OperationTaskScheduler(Operation operation) : TaskScheduler
{
    public override int MaximumConcurrencyLevel => 1;

    internal void Execute(Task task) => TryExecuteTask(task);

    protected override void QueueTask(Task task) => operation.Runtime.Enqueue(operation, task);

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
        // Inline only on the scheduler thread while this very operation is running: that keeps execution serialized
        // and lets RunSynchronously / Parallel.* / Wait-on-own-task work instead of hanging.
        operation.Runtime.IsSchedulerThread && operation.Runtime.IsRunning(operation) && TryExecuteTask(task);

    protected override IEnumerable<Task> GetScheduledTasks() => [.. operation.Queue];
}
