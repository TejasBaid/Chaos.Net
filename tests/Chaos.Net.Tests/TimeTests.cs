using System.Diagnostics;

namespace ChaosNet.Tests;

public sealed class TimeTests
{
    [Fact]
    public void Virtual_delays_complete_instantly_and_advance_the_clock()
    {
        var sw = Stopwatch.StartNew();
        TestReport report = TestEngine.Run(async () =>
        {
            DateTimeOffset start = Chaos.UtcNow;
            await ControlledTask.Delay(TimeSpan.FromHours(1));
            await Task.Delay(TimeSpan.FromMinutes(5), Chaos.TimeProvider);
            Chaos.Assert(Chaos.UtcNow - start == TimeSpan.FromMinutes(65), $"elapsed {Chaos.UtcNow - start}");
        }, new TestConfig { Iterations = 50, Strategy = Strategies.Random(1) });
        Assert.False(report.HasBugs, report.ToString());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    private static async Task<int> CountTicks()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10), Chaos.TimeProvider);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), Chaos.TimeProvider);
        int ticks = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(cts.Token))
            {
                ticks++;
            }
        }
        catch (OperationCanceledException)
        {
        }

        return ticks;
    }

    [Fact]
    public void PeriodicTimer_and_CancelAfter_work_on_virtual_time()
    {
        // On an infinitely fast machine the consumer sees every tick before the 10 s cancellation (ticks 1..9; at
        // t=10 s the cancellation timer, armed first, wins the tie).
        TestReport idle = TestEngine.Run(async () =>
        {
            int ticks = await CountTicks();
            Chaos.Assert(ticks == 9, $"ticks = {ticks}");
        }, new TestConfig { Iterations = 20, Strategy = Strategies.Random(2), TimerPolicy = TimerPolicy.FireWhenIdle });
        Assert.False(idle.HasBugs, idle.ToString());

        // With a nondeterministic clock the consumer may be slow, and PeriodicTimer coalesces missed ticks.
        var observed = new HashSet<int>();
        TestReport slow = TestEngine.Run(async () =>
        {
            int ticks = await CountTicks();
            observed.Add(ticks);
            Chaos.Assert(ticks is >= 0 and <= 9, $"ticks = {ticks}");
        }, new TestConfig { Iterations = 200, Strategy = Strategies.Random(2) });
        Assert.False(slow.HasBugs, slow.ToString());
        Assert.True(observed.Count > 3, "a slow consumer should observe different numbers of coalesced ticks");
    }

    private static async Task TimeoutRace()
    {
        Task work = ControlledTask.Run(async () =>
        {
            for (int i = 0; i < 3; i++)
            {
                await Chaos.Interleave();
            }
        }, "work");
        Task timeout = ControlledTask.Delay(TimeSpan.FromSeconds(30));
        Task winner = await Task.WhenAny(work, timeout);
        Chaos.Assert(winner == work, "the 30 s timeout fired before 3 steps of work finished");
    }

    [Fact]
    public void Nondeterministic_timers_find_timeout_races()
    {
        TestReport report = TestEngine.Run(TimeoutRace, new TestConfig { Iterations = 100, Strategy = Strategies.Random(3) });
        Assert.Equal(BugKind.AssertionFailure, report.FirstBug?.Kind);
    }

    [Fact]
    public void FireWhenIdle_models_an_infinitely_fast_machine()
    {
        TestReport report = TestEngine.Run(TimeoutRace, new TestConfig { Iterations = 1000, Strategy = Strategies.Dfs(), TimerPolicy = TimerPolicy.FireWhenIdle });
        Assert.False(report.HasBugs, report.ToString());
        Assert.True(report.SearchExhausted);
    }

    private static async Task StaggeredDelays()
    {
        DateTimeOffset last = Chaos.UtcNow;
        Task[] delays = [.. new[] { 30, 10, 20 }.Select(ms => ControlledTask.Run(async () =>
        {
            DateTimeOffset start = Chaos.UtcNow;
            await ControlledTask.Delay(ms);
            Chaos.Assert(Chaos.UtcNow >= last, "time went backwards");
            Chaos.Assert(Chaos.UtcNow - start >= TimeSpan.FromMilliseconds(ms), "delay completed early");
            last = Chaos.UtcNow;
        }))];
        await Task.WhenAll(delays);
    }

    [Fact]
    public void Timers_never_fire_early_and_time_never_goes_backwards()
    {
        TestReport report = TestEngine.Run(StaggeredDelays, new TestConfig { Iterations = 300, Strategy = Strategies.Random(4) });
        Assert.False(report.HasBugs, report.ToString());
    }

    [Fact]
    public void With_FireWhenIdle_all_delays_start_at_time_zero()
    {
        TestReport report = TestEngine.Run(async () =>
        {
            await StaggeredDelays();
            Chaos.Assert(Chaos.UtcNow == VirtualEpochPlus(30), $"clock at {Chaos.UtcNow:O}");
        }, new TestConfig { Iterations = 300, Strategy = Strategies.Random(4), TimerPolicy = TimerPolicy.FireWhenIdle });
        Assert.False(report.HasBugs, report.ToString());
    }

    private static DateTimeOffset VirtualEpochPlus(int ms) => new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(ms);

    [Fact]
    public void Canceled_controlled_delay_completes_as_canceled()
    {
        TestReport report = TestEngine.Run(async () =>
        {
            using var cts = new CancellationTokenSource();
            Task delay = ControlledTask.Delay(TimeSpan.FromDays(1), cts.Token);
            cts.Cancel(); // synchronous: CancelAsync would run the callbacks on the thread pool
            try
            {
                await delay;
                Chaos.Fail("delay should have been canceled");
            }
            catch (OperationCanceledException)
            {
            }
        }, new TestConfig { Iterations = 5 });
        Assert.False(report.HasBugs, report.ToString());
    }

    [Fact]
    public void Outside_a_test_the_time_provider_is_the_system_clock()
    {
        DateTimeOffset now = Chaos.UtcNow;
        Assert.True((DateTimeOffset.UtcNow - now).Duration() < TimeSpan.FromMinutes(1));
    }
}
