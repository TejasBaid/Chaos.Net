namespace ChaosNet.Tests;

public sealed class DfsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Enumerates_exactly_the_binomial_number_of_interleavings_of_two_workers(int yields)
    {
        // Each worker is yields+1 atomic steps; the root is blocked in WhenAll, so the executions are exactly the
        // interleavings of two sequences of length s = yields+1: C(2s, s).
        int s = yields + 1;
        TestReport report = TestEngine.Run(TestPrograms.TwoWorkers(yields), new TestConfig { Iterations = 100_000, Strategy = Strategies.Dfs() });

        Assert.True(report.SearchExhausted);
        Assert.Equal(TestPrograms.Binomial(2 * s, s), report.Iterations);
        Assert.Equal(report.Iterations, report.DistinctSchedules);
    }

    [Fact]
    public void Enumerates_the_multinomial_number_of_interleavings_of_three_workers()
    {
        // Steps per worker: 2, 3, 2 -> 7! / (2! 3! 2!) = 210
        TestReport report = TestEngine.Run(TestPrograms.Workers(1, 2, 1), new TestConfig { Iterations = 100_000, Strategy = Strategies.Dfs() });

        Assert.True(report.SearchExhausted);
        Assert.Equal(210, report.Iterations);
        Assert.Equal(210, report.DistinctSchedules);
    }

    [Fact]
    public void Controlled_nondeterminism_is_a_branching_point()
    {
        TestReport report = TestEngine.Run(() =>
        {
            _ = Chaos.RandomBool();
            _ = Chaos.RandomInt(3);
            _ = Chaos.RandomBool();
            return Task.CompletedTask;
        }, new TestConfig { Iterations = 1000, Strategy = Strategies.Dfs() });

        Assert.True(report.SearchExhausted);
        Assert.Equal(2 * 3 * 2, report.Iterations);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Preemption_bound_zero_and_one_give_the_closed_form_counts(int yields)
    {
        int s = yields + 1;
        TestReport pb0 = TestEngine.Run(TestPrograms.TwoWorkers(yields), new TestConfig { Iterations = 100_000, Strategy = Strategies.Dfs(preemptionBound: 0) });
        TestReport pb1 = TestEngine.Run(TestPrograms.TwoWorkers(yields), new TestConfig { Iterations = 100_000, Strategy = Strategies.Dfs(preemptionBound: 1) });
        TestReport all = TestEngine.Run(TestPrograms.TwoWorkers(yields), new TestConfig { Iterations = 100_000, Strategy = Strategies.Dfs() });

        // No preemption: run A to completion then B, or B then A.
        Assert.Equal(2, pb0.Iterations);

        // One preemption: pick who starts (2), then either never preempt or preempt after k of its s steps (k = 1..s-1).
        Assert.Equal(2 * s, pb1.Iterations);
        Assert.True(pb1.Iterations <= all.Iterations);
        Assert.True(pb0.SearchExhausted && pb1.SearchExhausted);
    }

    [Fact]
    public void Finds_the_lost_update_and_exhausts_the_fixed_cache()
    {
        TestReport buggy = TestEngine.Run(TestPrograms.LostUpdate, new TestConfig { Iterations = 1000, Strategy = Strategies.Dfs() });
        Assert.Equal(BugKind.AssertionFailure, buggy.FirstBug?.Kind);

        TestReport fixedCache = TestEngine.Run(Samples.AsyncCacheSample.FixedTest, new TestConfig { Iterations = 100_000, Strategy = Strategies.Dfs() });
        Assert.False(fixedCache.HasBugs, fixedCache.ToString());
        Assert.True(fixedCache.SearchExhausted);
        Assert.Equal(fixedCache.Iterations, fixedCache.DistinctSchedules);
    }

    [Fact]
    public void Depth_bound_is_reported()
    {
        var dfs = Strategies.Dfs(maxDepth: 3);
        TestReport report = TestEngine.Run(TestPrograms.TwoWorkers(5), new TestConfig { Iterations = 100_000, Strategy = dfs });
        Assert.True(report.SearchExhausted);
        Assert.True(dfs.DepthBoundHit);
        Assert.True(report.Iterations < TestPrograms.Binomial(12, 6));
    }
}
