using System.Globalization;
using System.Text;
using ChaosNet.Tracing;

namespace ChaosNet;

/// <summary>A bug found by a systematic test, with everything needed to understand and reproduce it.</summary>
public sealed class BugReport
{
    internal BugReport(BugKind kind, string message, Exception? exception, int iteration, int steps, ScheduleTrace trace)
    {
        Kind = kind;
        Message = message;
        Exception = exception;
        Iteration = iteration;
        Steps = steps;
        Trace = trace;
    }

    /// <summary>Gets the bug class.</summary>
    public BugKind Kind { get; }

    /// <summary>Gets the bug description.</summary>
    public string Message { get; }

    /// <summary>Gets the exception behind the bug, if any.</summary>
    public Exception? Exception { get; }

    /// <summary>Gets the zero-based iteration that found the bug.</summary>
    public int Iteration { get; }

    /// <summary>Gets the number of scheduling steps of the buggy execution.</summary>
    public int Steps { get; }

    /// <summary>Gets the replayable schedule trace.</summary>
    public ScheduleTrace Trace { get; internal set; }

    /// <summary>Gets the iteration seed, if the strategy was randomized.</summary>
    public ulong? Seed => Trace.Seed;

    /// <summary>Gets a C# expression that recreates a strategy reproducing the bug in its first iteration, if available.</summary>
    public string? Reproduction => Trace.Reproduction;

    /// <summary>Gets the path the trace was written to, if <see cref="TestConfig.TraceDirectory"/> was set.</summary>
    public string? TracePath { get; internal set; }

    /// <summary>Gets the rendered interleaving timeline, if it was recorded.</summary>
    public string? Timeline { get; internal set; }

    /// <summary>Gets whether replaying the trace reproduced the same bug on the same schedule (<see langword="null"/> if not checked).</summary>
    public bool? ReplayVerified { get; internal set; }

    /// <inheritdoc />
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{Kind} found in iteration {Iteration} after {Steps} steps").AppendLine();
        sb.AppendLine(Message);
        if (Exception is not null && Kind == BugKind.UnhandledException)
        {
            sb.AppendLine(Exception.ToString());
        }

        sb.AppendLine();
        sb.Append("Strategy: ").AppendLine(Trace.Strategy);
        if (Trace.SeedHex is not null)
        {
            sb.Append("Iteration seed: ").AppendLine(Trace.SeedHex);
        }

        if (Reproduction is not null)
        {
            sb.Append("Reproduce: new TestConfig { Iterations = 1, Strategy = ").Append(Reproduction).AppendLine(" }");
        }

        if (ReplayVerified is { } verified)
        {
            sb.AppendLine(verified ? "Replay: verified (trace re-executed and reproduced the bug on the identical schedule)" : "Replay: NOT reproduced; the test is nondeterministic under control");
        }

        if (TracePath is not null)
        {
            sb.Append("Trace file: ").AppendLine(TracePath);
        }

        sb.Append("Schedule: ").AppendLine(Trace.Schedule.Length <= 400 ? Trace.Schedule : Trace.Schedule[..400] + " ...");
        if (Timeline is not null)
        {
            sb.AppendLine().AppendLine(Timeline);
        }

        return sb.ToString();
    }
}

/// <summary>The result of a systematic test run.</summary>
public sealed class TestReport
{
    internal TestReport()
    {
    }

    /// <summary>Gets the test name, if configured.</summary>
    public string? TestName { get; internal init; }

    /// <summary>Gets a description of the strategy.</summary>
    public string Strategy { get; internal init; } = string.Empty;

    /// <summary>Gets the number of iterations executed.</summary>
    public int Iterations { get; internal init; }

    /// <summary>Gets the number of iterations that found a bug.</summary>
    public int BuggyIterations { get; internal init; }

    /// <summary>Gets the bugs found (at most <see cref="TestConfig.MaxBugsReported"/>).</summary>
    public IReadOnlyList<BugReport> Bugs { get; internal init; } = [];

    /// <summary>Gets the first bug found, if any.</summary>
    public BugReport? FirstBug => Bugs.Count > 0 ? Bugs[0] : null;

    /// <summary>Gets whether a bug was found.</summary>
    public bool HasBugs => Bugs.Count > 0;

    /// <summary>Gets the wall-clock duration of the run.</summary>
    public TimeSpan Elapsed { get; internal init; }

    /// <summary>Gets the total number of scheduling steps over all iterations.</summary>
    public long TotalSteps { get; internal init; }

    /// <summary>Gets the largest number of steps in one iteration.</summary>
    public int MaxStepsInIteration { get; internal init; }

    /// <summary>Gets the number of distinct schedules (decision sequences) explored.</summary>
    public int DistinctSchedules { get; internal init; }

    /// <summary>Gets the number of iterations that were cut off by <see cref="TestConfig.MaxSteps"/>.</summary>
    public int IterationsHitStepBound { get; internal init; }

    /// <summary>Gets whether the strategy exhausted its search space (DFS): every execution within the bounds ran.</summary>
    public bool SearchExhausted { get; internal init; }

    /// <summary>Gets warnings (for example tolerated uncontrolled concurrency).</summary>
    public IReadOnlyList<string> Warnings { get; internal init; } = [];

    /// <summary>For replays: whether the replay reproduced the trace's bug on the identical schedule.</summary>
    public bool? Reproduced { get; internal init; }

    /// <summary>For replays: the rendered timeline of the replayed execution (bug or not).</summary>
    public string? Timeline { get; internal init; }

    /// <summary>Throws <see cref="ChaosTestFailedException"/> describing the first bug, if any.</summary>
    public void ThrowIfBug()
    {
        if (FirstBug is not null)
        {
            throw new ChaosTestFailedException(this);
        }
    }

    /// <summary>Returns a one-paragraph summary.</summary>
    public string Summary()
    {
        double rate = Elapsed.TotalSeconds > 0 ? Iterations / Elapsed.TotalSeconds : 0;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"{(TestName is null ? string.Empty : TestName + ": ")}{(HasBugs ? "BUG FOUND" : "no bugs found")} | strategy {Strategy} | " +
            $"{Iterations} iterations ({DistinctSchedules} distinct schedules) in {Elapsed.TotalMilliseconds:0} ms ({rate:0}/s) | " +
            $"{TotalSteps} steps, max {MaxStepsInIteration}/iteration");
        if (SearchExhausted)
        {
            sb.Append(" | search space exhausted");
        }

        if (IterationsHitStepBound > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" | {IterationsHitStepBound} iterations hit the step bound");
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var sb = new StringBuilder(Summary()).AppendLine();
        foreach (string w in Warnings.Take(5))
        {
            sb.Append("warning: ").AppendLine(w);
        }

        if (FirstBug is not null)
        {
            sb.AppendLine().Append(FirstBug);
        }
        else if (Timeline is not null)
        {
            sb.AppendLine().Append(Timeline);
        }

        return sb.ToString();
    }
}

/// <summary>Thrown by <see cref="TestReport.ThrowIfBug"/>; its message is the full bug report.</summary>
public sealed class ChaosTestFailedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ChaosTestFailedException(TestReport report)
        : base((report ?? throw new ArgumentNullException(nameof(report))).ToString()) => Report = report;

    /// <summary>Gets the report.</summary>
    public TestReport Report { get; }
}
