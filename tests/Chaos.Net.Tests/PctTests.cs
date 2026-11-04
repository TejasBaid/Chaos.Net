namespace ChaosNet.Tests;

public sealed class PctTests
{
    private static int? IterationsToBug(ISchedulingStrategy strategy, Func<Task> test, int cap = 20_000) =>
        TestEngine.Run(test, new TestConfig { Iterations = cap, Strategy = strategy, VerifyBugsByReplay = false }).FirstBug is { } bug ? bug.Iteration + 1 : null;

    [Fact]
    public void Pct_depth3_finds_the_depth3_bug_that_depth2_and_random_walk_miss()
    {
        var pct3 = new List<int>();
        for (ulong seed = 1; seed <= 10; seed++)
        {
            int? n = IterationsToBug(Strategies.Pct(3, seed), Samples.CheckpointSample.BuggyTest);
            Assert.NotNull(n);
            pct3.Add(n.Value);
        }

        Assert.True(pct3.Average() < 500, $"PCT d=3 mean {pct3.Average()}");
        Assert.Null(IterationsToBug(Strategies.Pct(2, 1), Samples.CheckpointSample.BuggyTest, cap: 5000));
        Assert.Null(IterationsToBug(Strategies.Random(1), Samples.CheckpointSample.BuggyTest, cap: 5000));
    }

    [Fact]
    public void Pct_uses_depth_minus_one_change_points_once_k_is_known()
    {
        var pct = Strategies.Pct(depth: 4, seed: 3);
        TestEngine.Run(TestPrograms.Busy, new TestConfig { Iterations = 5, Strategy = pct });
        pct.PrepareIteration(1);
        Assert.Equal(3, pct.CurrentChangePoints.Count);
        Assert.Equal(pct.CurrentChangePoints.Distinct().Count(), pct.CurrentChangePoints.Count);
    }

    [Fact]
    public void Pct_depth1_without_change_points_runs_each_operation_to_completion_in_priority_order()
    {
        // With d = 1 there are no change points: the highest-priority enabled operation always runs, so the two
        // workers never interleave: each execution is A-then-B or B-then-A.
        var schedules = new HashSet<string>();
        TestEngine.Run(TestPrograms.TwoWorkers(3), new TestConfig
        {
            Iterations = 200,
            Strategy = Strategies.Pct(1, 8),
            OnIterationCompleted = s => schedules.Add(Tracing.ScheduleCodec.Encode(s.Decisions)),
        });
        Assert.Equal(2, schedules.Count);
        Assert.Contains("0 1*4 2*4 0", schedules);
        Assert.Contains("0 2*4 1*4 0", schedules);
    }

    [Fact]
    public void Portfolio_alternates_strategies()
    {
        var names = new List<string>();
        var portfolio = Strategies.Portfolio(Strategies.Random(1), Strategies.Pct(2, 1));
        TestEngine.Run(TestPrograms.Busy, new TestConfig
        {
            Iterations = 4,
            Strategy = portfolio,
            OnIterationCompleted = _ => names.Add(portfolio.Current.Name),
        });
        Assert.Equal(["random", "pct", "random", "pct"], names);
    }
}
