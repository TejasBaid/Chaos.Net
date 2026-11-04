using ChaosNet.Tracing;

namespace ChaosNet.Tests;

public sealed class DeterminismTests
{
    private static List<ulong> ScheduleHashes(ISchedulingStrategy strategy, Func<Task> test, int iterations)
    {
        var hashes = new List<ulong>();
        TestReport report = TestEngine.Run(test, new TestConfig
        {
            Iterations = iterations,
            Strategy = strategy,
            OnIterationCompleted = s => hashes.Add(s.ScheduleHash),
        });
        Assert.False(report.HasBugs, report.ToString());
        return hashes;
    }

    [Theory]
    [InlineData("random")]
    [InlineData("pct")]
    public void Same_seed_produces_the_same_sequence_of_schedules(string strategy)
    {
        ISchedulingStrategy Make() => strategy == "random" ? Strategies.Random(12345) : Strategies.Pct(3, 12345);

        List<ulong> first = ScheduleHashes(Make(), TestPrograms.Busy, 300);
        List<ulong> second = ScheduleHashes(Make(), TestPrograms.Busy, 300);

        Assert.Equal(first, second);
        Assert.True(first.Distinct().Count() > 50, "the program should have many distinct schedules");
    }

    [Fact]
    public void Different_seeds_explore_different_schedules()
    {
        List<ulong> a = ScheduleHashes(Strategies.Random(1), TestPrograms.Busy, 100);
        List<ulong> b = ScheduleHashes(Strategies.Random(2), TestPrograms.Busy, 100);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Related_base_seeds_do_not_share_iteration_streams()
    {
        // Regression: iteration seeds used to be base + i*phi, so seed s+phi was seed s shifted by one iteration.
        List<ulong> a = ScheduleHashes(Strategies.Random(1), TestPrograms.Busy, 50);
        List<ulong> b = ScheduleHashes(Strategies.Random(2), TestPrograms.Busy, 50);
        Assert.True(a.Skip(1).Intersect(b).Count() < 5);
    }

    [Fact]
    public void Same_seed_finds_the_same_bug_at_the_same_iteration_with_the_same_trace()
    {
        TestReport r1 = TestEngine.Run(Samples.LeaderElectionSample.BuggyTest, new TestConfig { Iterations = 5000, Strategy = Strategies.Random(99) });
        TestReport r2 = TestEngine.Run(Samples.LeaderElectionSample.BuggyTest, new TestConfig { Iterations = 5000, Strategy = Strategies.Random(99) });

        Assert.True(r1.HasBugs);
        Assert.Equal(r1.FirstBug!.Iteration, r2.FirstBug!.Iteration);
        Assert.Equal(r1.FirstBug.Trace.Schedule, r2.FirstBug.Trace.Schedule);
        Assert.Equal(r1.FirstBug.Message, r2.FirstBug.Message);
        Assert.Equal(r1.FirstBug.Timeline, r2.FirstBug.Timeline);
    }

    [Theory]
    [InlineData("random")]
    [InlineData("pct")]
    public void Reproduction_hint_replays_a_late_iteration_as_iteration_zero(string strategy)
    {
        // Random walk needs a few hundred iterations on the lease bug; PCT needs tens on the small checkpoint bug.
        Func<Task> program = strategy == "random" ? Samples.LeaseFencingSample.BuggyTest : () => Samples.CheckpointSample.RunAsync(buggy: true, cellsPerTransaction: 3);
        ISchedulingStrategy search = strategy == "random" ? Strategies.Random(7) : Strategies.Pct(3, 7);
        TestReport report = TestEngine.Run(program, new TestConfig { Iterations = 100_000, Strategy = search, MaxSteps = 2000 });
        BugReport bug = report.FirstBug ?? throw new InvalidOperationException(report.ToString());
        Assert.True(bug.Iteration > 0);

        // Rebuild the strategy from the hint's parameters (what a user would paste into code).
        ulong seed = bug.Seed!.Value;
        ISchedulingStrategy again = strategy == "random"
            ? Strategies.Random(seed)
            : Strategies.Pct(3, seed, stepsEstimate: int.Parse(System.Text.RegularExpressions.Regex.Match(bug.Reproduction!, @"stepsEstimate: (\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        TestReport rerun = TestEngine.Run(program, new TestConfig { Iterations = 1, Strategy = again, MaxSteps = 2000 });

        Assert.Equal(0, rerun.FirstBug!.Iteration);
        Assert.Equal(bug.Trace.Schedule, rerun.FirstBug.Trace.Schedule);
    }

    [Fact]
    public void Decision_log_records_every_choice_in_order()
    {
        var summaries = new List<IterationSummary>();
        TestEngine.Run(async () =>
        {
            _ = Chaos.RandomBool();
            _ = Chaos.RandomInt(5);
            await ControlledTask.Run(async () => await Chaos.Interleave());
        }, new TestConfig { Iterations = 1, Strategy = Strategies.Random(3), OnIterationCompleted = summaries.Add });

        IReadOnlyList<Decision> d = summaries.Single().Decisions;
        Assert.Equal(DecisionKind.Schedule, d[0].Kind); // root's first step
        Assert.Equal(DecisionKind.Boolean, d[1].Kind);
        Assert.Equal(DecisionKind.Integer, d[2].Kind);
        Assert.InRange(d[2].Value, 0, 4);
        Assert.All(d.Skip(3), x => Assert.Equal(DecisionKind.Schedule, x.Kind));
    }
}
