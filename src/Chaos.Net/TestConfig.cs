using ChaosNet.Scheduling;
using ChaosNet.Tracing;

namespace ChaosNet;

/// <summary>How virtual timers (<see cref="ControlledTask.Delay(TimeSpan, CancellationToken)"/>, <see cref="Chaos.TimeProvider"/> timers) interleave with other work.</summary>
public enum TimerPolicy
{
    /// <summary>
    /// The clock is an ordinary schedulable choice whenever a timer is pending, so the strategy decides when time
    /// advances relative to other work. This models arbitrary processing delays (GC pauses, slow I/O) and finds timeout
    /// races. Timers still fire in due-time order and virtual time never goes backwards.
    /// </summary>
    Nondeterministic,

    /// <summary>Timers fire only when no operation is enabled. This models an infinitely fast machine: work always finishes before time advances.</summary>
    FireWhenIdle,
}

/// <summary>What to do when work escapes the controlled scheduler.</summary>
public enum UncontrolledConcurrencyPolicy
{
    /// <summary>Report a <see cref="BugKind.UncontrolledConcurrency"/> bug. This is the default, because replay is no longer guaranteed.</summary>
    Fail,

    /// <summary>Accept the work (it is queued to its operation the next time the scheduler is idle) and record a warning.</summary>
    Tolerate,
}

/// <summary>Configuration of a systematic test run.</summary>
public sealed record TestConfig
{
    /// <summary>Gets the maximum number of iterations (executions) to explore. Default: 1000.</summary>
    public int Iterations { get; init; } = 1000;

    /// <summary>Gets the exploration strategy. Default: <see cref="Strategies.Random(ulong?)"/> with a fresh seed.</summary>
    public ISchedulingStrategy? Strategy { get; init; }

    /// <summary>Gets the bound on scheduling steps per iteration. Default: 10,000.</summary>
    public int MaxSteps { get; init; } = 10_000;

    /// <summary>
    /// Gets after how many steps an unfair strategy (PCT, DFS) is replaced by a seeded fair random walk for the rest of
    /// the iteration. Defaults to half of <see cref="MaxSteps"/>.
    /// </summary>
    public int? MaxUnfairSteps { get; init; }

    /// <summary>Gets whether hitting <see cref="MaxSteps"/> before the test body completes is reported as a <see cref="BugKind.Livelock"/>. Default: <see langword="true"/>.</summary>
    public bool ReportLivelocks { get; init; } = true;

    /// <summary>Gets the number of consecutive steps a <see cref="ChaosMonitor"/> may stay hot before a liveness violation is reported. 0 (the default) only checks at the end of an iteration.</summary>
    public int LivenessTemperatureThreshold { get; init; }

    /// <summary>Gets the timer policy. Default: <see cref="TimerPolicy.Nondeterministic"/>.</summary>
    public TimerPolicy TimerPolicy { get; init; } = TimerPolicy.Nondeterministic;

    /// <summary>Gets the policy for work that escapes the scheduler. Default: <see cref="UncontrolledConcurrencyPolicy.Fail"/>.</summary>
    public UncontrolledConcurrencyPolicy UncontrolledConcurrency { get; init; } = UncontrolledConcurrencyPolicy.Fail;

    /// <summary>
    /// Gets how long the scheduler waits for uncontrolled work (real I/O, thread-pool callbacks) to post back before it
    /// declares a deadlock. Default: zero, because controlled code never needs it.
    /// </summary>
    public TimeSpan UncontrolledGracePeriod { get; init; } = TimeSpan.Zero;

    /// <summary>Gets whether an exception (other than cancellation) that faults a background operation is a bug even if someone might observe it later. Default: <see langword="true"/>.</summary>
    public bool FailOnUnhandledExceptionsInOperations { get; init; } = true;

    /// <summary>
    /// Gets whether operations still blocked on a lock, semaphore or full channel after the test body has completed are
    /// reported as a deadlock. Operations waiting to <em>receive</em> are never reported, because idle server loops are
    /// normal. Default: <see langword="true"/>.
    /// </summary>
    public bool ReportBlockedOperationsAsDeadlock { get; init; } = true;

    /// <summary>Gets whether the run stops at the first buggy iteration. Default: <see langword="true"/>.</summary>
    public bool StopOnFirstBug { get; init; } = true;

    /// <summary>Gets an optional wall-clock budget for the whole run.</summary>
    public TimeSpan? TimeLimit { get; init; }

    /// <summary>Gets the wall-clock limit for a single iteration before it is reported as a <see cref="BugKind.Hang"/>. Default: 10 seconds.</summary>
    public TimeSpan IterationTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets whether every bug is re-executed from its trace (with full event recording) before it is reported. Default: <see langword="true"/>.</summary>
    public bool VerifyBugsByReplay { get; init; } = true;

    /// <summary>Gets the directory bug traces are written to, or <see langword="null"/> to not write files.</summary>
    public string? TraceDirectory { get; init; }

    /// <summary>Gets the test name recorded in traces (<c>Namespace.Type.Method</c>), used by the replay CLI.</summary>
    public string? TestName { get; init; }

    /// <summary>Gets the path of the assembly containing the test, recorded in traces for the replay CLI.</summary>
    public string? TestAssemblyPath { get; init; }

    /// <summary>Gets the maximum number of distinct bugs kept in the report when <see cref="StopOnFirstBug"/> is off. Default: 10.</summary>
    public int MaxBugsReported { get; init; } = 10;

    /// <summary>Gets a callback invoked (on the scheduler thread) after every iteration.</summary>
    public Action<IterationSummary>? OnIterationCompleted { get; init; }

    internal int EffectiveMaxUnfairSteps => MaxUnfairSteps ?? Math.Max(1, MaxSteps / 2);

    internal TraceSettings ToTraceSettings() => new()
    {
        MaxSteps = MaxSteps,
        ReportLivelocks = ReportLivelocks,
        TimerPolicy = TimerPolicy,
        UncontrolledConcurrency = UncontrolledConcurrency,
        FailOnUnhandledExceptionsInOperations = FailOnUnhandledExceptionsInOperations,
        LivenessTemperatureThreshold = LivenessTemperatureThreshold,
        ReportBlockedOperationsAsDeadlock = ReportBlockedOperationsAsDeadlock,
    };

    internal TestConfig WithTraceSettings(TraceSettings s) => this with
    {
        MaxSteps = s.MaxSteps,
        ReportLivelocks = s.ReportLivelocks,
        TimerPolicy = s.TimerPolicy,
        UncontrolledConcurrency = s.UncontrolledConcurrency,
        FailOnUnhandledExceptionsInOperations = s.FailOnUnhandledExceptionsInOperations,
        LivenessTemperatureThreshold = s.LivenessTemperatureThreshold,
        ReportBlockedOperationsAsDeadlock = s.ReportBlockedOperationsAsDeadlock,
    };
}

/// <summary>Information about a finished iteration, passed to <see cref="TestConfig.OnIterationCompleted"/>.</summary>
/// <param name="Iteration">Zero-based iteration index.</param>
/// <param name="Steps">Scheduling steps taken.</param>
/// <param name="ScheduleHash">Stable hash of the decision sequence (equal hashes mean identical executions).</param>
/// <param name="Decisions">The decision sequence.</param>
/// <param name="Bug">The bug found, if any.</param>
/// <param name="Seed">The iteration seed, if the strategy is randomized.</param>
public sealed record IterationSummary(int Iteration, int Steps, ulong ScheduleHash, IReadOnlyList<Decision> Decisions, BugKind? Bug, ulong? Seed);
