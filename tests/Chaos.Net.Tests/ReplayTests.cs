using ChaosNet.Samples;
using ChaosNet.Tracing;

namespace ChaosNet.Tests;

public sealed class ReplayTests
{
    public static TheoryData<string> SampleNames => [.. SampleCatalog.All.Select(s => s.Name)];

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Every_bug_reproduces_exactly_from_its_trace(string name)
    {
        SampleInfo sample = SampleCatalog.Get(name);
        TestReport found = TestEngine.Run(sample.Buggy, new TestConfig { Iterations = 50_000, Strategy = Strategies.Pct(sample.PctDepth, 11), MaxSteps = sample.MaxSteps });
        BugReport bug = found.FirstBug ?? throw new InvalidOperationException(found.ToString());
        Assert.True(bug.ReplayVerified, "the engine verifies every bug by replay before reporting it");

        // Replay many times: same bug, same schedule, byte-identical timeline every time.
        string? timeline = null;
        for (int i = 0; i < 10; i++)
        {
            TestReport replay = TestEngine.Replay(sample.Buggy, bug.Trace);
            Assert.True(replay.Reproduced, replay.ToString());
            Assert.Equal(bug.Kind, replay.FirstBug!.Kind);
            Assert.Equal(bug.Message, replay.FirstBug.Message);
            timeline ??= replay.Timeline;
            Assert.Equal(timeline, replay.Timeline);
        }

        Assert.Equal(bug.Timeline, timeline);
    }

    [Fact]
    public void Trace_round_trips_through_a_file_and_replays()
    {
        string dir = Path.Combine(Path.GetTempPath(), "chaos-net-tests", Guid.NewGuid().ToString("N"));
        try
        {
            TestReport found = TestEngine.Run(LeaderElectionSample.BuggyTest, new TestConfig
            {
                Iterations = 10_000,
                Strategy = Strategies.Random(5),
                TraceDirectory = dir,
                TestName = "ChaosNet.Samples.LeaderElectionSample.BuggyTest",
            });
            string path = found.FirstBug!.TracePath!;
            Assert.True(File.Exists(path));

            ScheduleTrace loaded = ScheduleTrace.Load(path);
            Assert.Equal(found.FirstBug.Trace.Schedule, loaded.Schedule);
            Assert.Equal(found.FirstBug.Trace.Decisions, loaded.Decisions);
            Assert.Equal(BugKind.AssertionFailure, loaded.BugKind);
            Assert.Equal("ChaosNet.Samples.LeaderElectionSample.BuggyTest", loaded.TestName);
            Assert.NotNull(loaded.Timeline);

            TestReport replay = TestEngine.Replay(LeaderElectionSample.BuggyTest, path);
            Assert.True(replay.Reproduced);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Replaying_against_a_different_program_reports_divergence()
    {
        TestReport found = TestEngine.Run(TestPrograms.LostUpdate, new TestConfig { Iterations = 100, Strategy = Strategies.Random(2) });
        TestReport replay = TestEngine.Replay(TestPrograms.TwoWorkers(5), found.FirstBug!.Trace);

        Assert.False(replay.Reproduced);
        Assert.Equal(BugKind.Nondeterminism, replay.FirstBug?.Kind);
    }

    [Fact]
    public void Replay_detects_a_program_that_is_nondeterministic_under_control()
    {
        int executions = 0;
        Task Flaky()
        {
            // Behaves differently on its second execution: the kind of hidden state a test body must not carry.
            bool extra = Interlocked.Increment(ref executions) > 1;
            return ControlledTask.Run(async () =>
            {
                await Chaos.Interleave();
                if (extra)
                {
                    _ = Chaos.RandomBool();
                }

                Chaos.Assert(false, "always fails");
            });
        }

        TestReport report = TestEngine.Run(Flaky, new TestConfig { Iterations = 1, Strategy = Strategies.Random(1) });
        Assert.Equal(BugKind.AssertionFailure, report.FirstBug?.Kind);
        Assert.False(report.FirstBug!.ReplayVerified);
    }

    [Fact]
    public void Codec_round_trips_and_compresses_runs()
    {
        var rng = new Random(3);
        var decisions = new List<Decision>();
        for (int i = 0; i < 2000; i++)
        {
            decisions.Add(rng.Next(4) switch
            {
                0 => new Decision(DecisionKind.Boolean, rng.Next(2)),
                1 => new Decision(DecisionKind.Integer, rng.Next(100)),
                2 => new Decision(DecisionKind.Schedule, SchedulableOperation.ClockId),
                _ => new Decision(DecisionKind.Schedule, rng.Next(3)),
            });
        }

        string encoded = ScheduleCodec.Encode(decisions);
        Assert.Equal(decisions, ScheduleCodec.Decode(encoded));

        var runs = Enumerable.Repeat(new Decision(DecisionKind.Schedule, 7), 500).ToList();
        Assert.Equal("7*500", ScheduleCodec.Encode(runs));
        Assert.Equal(runs, ScheduleCodec.Decode("7*500"));
    }

    [Fact]
    public void Trace_json_is_stable_and_versioned()
    {
        ScheduleTrace trace = ScheduleTrace.FromDecisions([new(DecisionKind.Schedule, 0), new(DecisionKind.Boolean, 1)], new TraceSettings { MaxSteps = 42 });
        string json = trace.ToJson();
        Assert.Contains("\"format\": \"chaos-trace/1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"schedule\": \"0 b1\"", json, StringComparison.Ordinal);
        ScheduleTrace back = ScheduleTrace.FromJson(json);
        Assert.Equal(42, back.Settings.MaxSteps);
        Assert.Throws<FormatException>(() => ScheduleTrace.FromJson(json.Replace("chaos-trace/1", "chaos-trace/999", StringComparison.Ordinal)));
    }

    [Fact]
    public void Timeline_shows_operations_and_shared_memory_events()
    {
        TestReport report = TestEngine.Run(TestPrograms.LostUpdate, new TestConfig { Iterations = 100, Strategy = Strategies.Random(2) });
        string timeline = report.FirstBug!.Timeline!;
        Assert.Contains("op1   inc1", timeline, StringComparison.Ordinal);
        Assert.Contains("read counter = 0", timeline, StringComparison.Ordinal);
        Assert.Contains("write counter := 1", timeline, StringComparison.Ordinal);
        Assert.Contains("BUG at step", timeline, StringComparison.Ordinal);
    }
}
