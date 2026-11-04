using ChaosNet.Samples;
using Xunit.Abstractions;

namespace ChaosNet.Tests;

public sealed class SampleTests(ITestOutputHelper output)
{
    public static TheoryData<string> Names => [.. SampleCatalog.All.Select(s => s.Name)];

    [Theory]
    [MemberData(nameof(Names))]
    public void Buggy_version_is_found_with_the_expected_bug_and_a_verified_trace(string name)
    {
        SampleInfo s = SampleCatalog.Get(name);
        TestReport report = TestEngine.Run(s.Buggy, new TestConfig { Iterations = 20_000, Strategy = Strategies.Pct(s.PctDepth, 2024), MaxSteps = s.MaxSteps });
        output.WriteLine(report.ToString());
        BugReport bug = report.FirstBug ?? throw new global::Xunit.Sdk.XunitException($"no bug found in {name}");
        Assert.Equal(s.ExpectedBug, bug.Kind);
        Assert.True(bug.ReplayVerified);
        Assert.NotNull(bug.Timeline);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Fixed_version_passes_random_and_pct_exploration(string name)
    {
        SampleInfo s = SampleCatalog.Get(name);
        TestReport report = TestEngine.Run(s.Fixed, new TestConfig
        {
            Iterations = 3000,
            Strategy = Strategies.Portfolio(Strategies.Random(7), Strategies.Pct(2, 8), Strategies.Pct(3, 9)),
            MaxSteps = s.MaxSteps,
        });
        output.WriteLine(report.Summary());
        Assert.False(report.HasBugs, report.ToString());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Fixed_version_passes_context_bounded_dfs(string name)
    {
        SampleInfo s = SampleCatalog.Get(name);
        TestReport report = TestEngine.Run(s.Fixed, new TestConfig { Iterations = 3000, Strategy = Strategies.Dfs(preemptionBound: 2), MaxSteps = s.MaxSteps });
        output.WriteLine(report.Summary());
        Assert.False(report.HasBugs, report.ToString());
    }

    /// <summary>
    /// The naive baseline: run the real (production-mode) system on the thread pool many times. It reports how often
    /// stress testing observes the bug. The assertion is only that the fixed version never fails, which also checks
    /// that the samples are correct outside the test harness. Whether stress finds the buggy version is
    /// timing-dependent by nature, which is the point.
    /// </summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void Naive_stress_baseline(string name)
    {
        SampleInfo s = SampleCatalog.Get(name);
        NaiveStressResult buggy = NaiveStress.Run(s.Buggy, maxRuns: 2000, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2), stopOnFirstFailure: false);
        output.WriteLine($"{name} buggy: {buggy.Runs} runs, {buggy.Failures} failures, {buggy.Hangs} hangs in {buggy.Elapsed.TotalMilliseconds:0} ms; first: {buggy.FirstFailure ?? "none"} (run {buggy.FirstFailureRun})");

        NaiveStressResult fixedRuns = NaiveStress.Run(s.Fixed, maxRuns: 2000, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1), stopOnFirstFailure: true);
        output.WriteLine($"{name} fixed: {fixedRuns.Runs} runs, {fixedRuns.Failures} failures, {fixedRuns.Hangs} hangs");
        Assert.False(fixedRuns.FoundBug, fixedRuns.FirstFailure);

        TestReport chaos = TestEngine.Run(s.Buggy, new TestConfig { Iterations = 20_000, Strategy = Strategies.Pct(s.PctDepth, 1), MaxSteps = s.MaxSteps, VerifyBugsByReplay = false });
        output.WriteLine($"{name} Chaos.Net PCT(d={s.PctDepth}): bug in iteration {chaos.FirstBug?.Iteration + 1} after {chaos.Elapsed.TotalMilliseconds:0.0} ms, deterministic and replayable");
        Assert.True(chaos.HasBugs);
    }

    [Fact]
    public void Lease_bug_is_unreachable_for_stress_but_found_systematically()
    {
        NaiveStressResult naive = NaiveStress.Run(LeaseFencingSample.BuggyTest, maxRuns: 1000, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2), stopOnFirstFailure: true);
        output.WriteLine($"naive: {naive.Runs} runs, found: {naive.FoundBug}");
        TestReport chaos = TestEngine.Run(LeaseFencingSample.BuggyTest, new TestConfig { Iterations = 1000, Strategy = Strategies.Pct(2, 1) });
        Assert.True(chaos.HasBugs);
        Assert.Contains("lost update", chaos.FirstBug!.Message, StringComparison.Ordinal);
        Assert.Contains("store accepts write", chaos.FirstBug.Timeline, StringComparison.Ordinal);
    }

    [Fact]
    public void Checkpoint_scaling_random_walk_degrades_while_pct_does_not()
    {
        int? Find(ISchedulingStrategy st, int n) => TestEngine.Run(() => CheckpointSample.RunAsync(true, n), new TestConfig { Iterations = 20_000, Strategy = st, VerifyBugsByReplay = false }).FirstBug?.Iteration;

        Assert.NotNull(Find(Strategies.Random(1), 2));   // short scan: random walk copes
        Assert.Null(Find(Strategies.Random(1), 8));      // 16-step scan: (1/3)^16, hopeless
        Assert.NotNull(Find(Strategies.Pct(3, 1), 2));
        Assert.NotNull(Find(Strategies.Pct(3, 1), 8));   // PCT's probability does not depend on the scan length
    }
}
