namespace ChaosNet;

/// <summary>The class of bug an iteration found.</summary>
public enum BugKind
{
    /// <summary><see cref="Chaos.Assert(bool, string?, string?)"/> (or a monitor assertion) failed.</summary>
    AssertionFailure,

    /// <summary>An exception escaped the test body, an operation, an <c>async void</c> method or a timer callback.</summary>
    UnhandledException,

    /// <summary>No operation could make progress: every live operation is blocked, and the test body has not finished or an operation is stuck acquiring a resource.</summary>
    Deadlock,

    /// <summary>The test body did not finish within <see cref="TestConfig.MaxSteps"/> scheduling steps although operations kept running.</summary>
    Livelock,

    /// <summary>A <see cref="ChaosMonitor"/> was hot at the end of the execution, or hotter for longer than the temperature threshold.</summary>
    LivenessViolation,

    /// <summary>Work escaped the controlled scheduler (thread pool, raw threads, <c>ConfigureAwait(false)</c>), so the execution is no longer deterministic.</summary>
    UncontrolledConcurrency,

    /// <summary>A replay or DFS prefix did not match the program's behaviour.</summary>
    Nondeterminism,

    /// <summary>An iteration exceeded <see cref="TestConfig.IterationTimeout"/> of wall-clock time, usually because code blocked the scheduler thread (<c>.Result</c>, <c>.Wait()</c>, <c>Thread.Sleep</c>, an infinite synchronous loop).</summary>
    Hang,
}
