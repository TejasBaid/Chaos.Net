using Xunit;
using Xunit.Sdk;

namespace ChaosNet.Xunit;

/// <summary>The exploration strategy used by <see cref="ChaosFactAttribute"/>.</summary>
public enum ChaosStrategy
{
    /// <summary>Random walk.</summary>
    Random,

    /// <summary>Probabilistic Concurrency Testing (see <see cref="ChaosFactAttribute.PctDepth"/>).</summary>
    Pct,

    /// <summary>Bounded exhaustive DFS (see <see cref="ChaosFactAttribute.PreemptionBound"/>).</summary>
    Dfs,

    /// <summary>Alternate random walk and PCT between iterations.</summary>
    Portfolio,
}

/// <summary>
/// Marks a test method that runs under Chaos.Net systematic concurrency testing. The method body is the test: it is
/// executed once per iteration, on a fresh instance of the test class, under controlled scheduling. The test fails
/// with the full bug report (timeline, seed, trace path) if any iteration finds a bug.
/// </summary>
/// <example>
/// <code>
/// [ChaosFact(Iterations = 5000, Strategy = ChaosStrategy.Pct, PctDepth = 2)]
/// public async Task Transfers_conserve_money()
/// {
///     var bank = new Bank(...);
///     await Task.WhenAll(ControlledTask.Run(() => bank.TransferAsync(0, 1, 10)), ControlledTask.Run(() => bank.TransferAsync(1, 0, 5)));
///     Chaos.Assert(bank.Total == 200);
/// }
/// </code>
/// </example>
[XunitTestCaseDiscoverer("ChaosNet.Xunit.ChaosFactDiscoverer", "Chaos.Net.Xunit")]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ChaosFactAttribute : FactAttribute
{
    /// <summary>Gets or sets the number of iterations. Default: 1000.</summary>
    public int Iterations { get; set; } = 1000;

    /// <summary>Gets or sets the strategy. Default: <see cref="ChaosStrategy.Portfolio"/>.</summary>
    public ChaosStrategy Strategy { get; set; } = ChaosStrategy.Portfolio;

    /// <summary>Gets or sets the PCT bug depth. Default: 3.</summary>
    public int PctDepth { get; set; } = 3;

    /// <summary>Gets or sets the DFS preemption bound; negative means unbounded. Default: -1.</summary>
    public int PreemptionBound { get; set; } = -1;

    /// <summary>Gets or sets the seed; 0 means a fresh random seed (printed on failure). Default: 0.</summary>
    public long Seed { get; set; }

    /// <summary>Gets or sets the per-iteration step bound. Default: 10,000.</summary>
    public int MaxSteps { get; set; } = 10_000;

    /// <summary>Gets or sets the timer policy. Default: <see cref="TimerPolicy.Nondeterministic"/>.</summary>
    public TimerPolicy TimerPolicy { get; set; } = TimerPolicy.Nondeterministic;

    /// <summary>Gets or sets whether hitting the step bound is a livelock. Default: <see langword="true"/>.</summary>
    public bool ReportLivelocks { get; set; } = true;

    /// <summary>Gets or sets the directory for bug traces, relative to the test output directory. Default: <c>chaos-traces</c>.</summary>
    public string TraceDirectory { get; set; } = "chaos-traces";

    /// <summary>
    /// Gets or sets whether the test is expected to find a bug. Useful for regression tests of the tool itself
    /// and for documenting known-buggy samples: the test passes only if a bug of <see cref="ExpectedBug"/> is found.
    /// </summary>
    public bool ExpectBug { get; set; }

    /// <summary>Gets or sets the expected bug kind when <see cref="ExpectBug"/> is set. Default: any kind.</summary>
    public BugKind ExpectedBug { get; set; } = (BugKind)(-1);

    /// <summary>Builds the <see cref="TestConfig"/> described by this attribute.</summary>
    public TestConfig ToConfig(string testName, string? assemblyPath)
    {
        ulong? seed = Seed == 0 ? null : unchecked((ulong)Seed);
        Scheduling.ISchedulingStrategy strategy = Strategy switch
        {
            ChaosStrategy.Random => Strategies.Random(seed),
            ChaosStrategy.Pct => Strategies.Pct(PctDepth, seed),
            ChaosStrategy.Dfs => Strategies.Dfs(preemptionBound: PreemptionBound < 0 ? null : PreemptionBound),
            _ => Strategies.Portfolio(
                Strategies.Random(seed),
                Strategies.Pct(PctDepth, seed is { } s ? s ^ 0x9E3779B97F4A7C15UL : null)),
        };

        return new TestConfig
        {
            Iterations = Iterations,
            Strategy = strategy,
            MaxSteps = MaxSteps,
            TimerPolicy = TimerPolicy,
            ReportLivelocks = ReportLivelocks,
            TestName = testName,
            TestAssemblyPath = assemblyPath,
            TraceDirectory = Path.IsPathRooted(TraceDirectory) ? TraceDirectory : Path.Combine(AppContext.BaseDirectory, TraceDirectory),
        };
    }
}
