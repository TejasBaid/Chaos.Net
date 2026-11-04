using System.Threading.Channels;

namespace ChaosNet.Tests;

public sealed class PrimitivesTests
{
    [Fact]
    public void AsyncLock_provides_mutual_exclusion_under_every_explored_schedule()
    {
        TestReport report = TestEngine.Run(async () =>
        {
            var gate = new AsyncLock("gate");
            int inside = 0;
            var counter = new SharedRegister<int>(0, "counter");
            async Task Worker()
            {
                for (int i = 0; i < 2; i++)
                {
                    using (await gate.AcquireAsync())
                    {
                        inside++;
                        Chaos.Assert(inside == 1, "two operations inside the critical section");
                        int v = await counter.ReadAsync();
                        await counter.WriteAsync(v + 1);
                        inside--;
                    }
                }
            }

            await Task.WhenAll(ControlledTask.Run(Worker), ControlledTask.Run(Worker), ControlledTask.Run(Worker));
            Chaos.Assert(counter.Value == 6);
        }, new TestConfig { Iterations = 2000, Strategy = Strategies.Portfolio(Strategies.Random(1), Strategies.Pct(3, 1)) });
        Assert.False(report.HasBugs, report.ToString());
    }

    [Fact]
    public void Semaphore_bounds_concurrency()
    {
        int maxObserved = 0;
        TestReport report = TestEngine.Run(async () =>
        {
            var sem = new ControlledSemaphore(2, name: "pool");
            int inside = 0;
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ControlledTask.Run(async () =>
            {
                await sem.WaitAsync();
                inside++;
                maxObserved = Math.Max(maxObserved, inside);
                Chaos.Assert(inside <= 2, "semaphore admitted too many");
                await Chaos.Interleave();
                inside--;
                sem.Release();
            })));
        }, new TestConfig { Iterations = 1000, Strategy = Strategies.Random(9) });
        Assert.False(report.HasBugs, report.ToString());
        Assert.Equal(2, maxObserved);
    }

    [Fact]
    public void Channel_is_fifo_and_bounded_send_blocks()
    {
        TestReport report = TestEngine.Run(async () =>
        {
            var ch = new ControlledChannel<int>(capacity: 1, "ch");
            Task producer = ControlledTask.Run(async () =>
            {
                for (int i = 0; i < 5; i++)
                {
                    await ch.SendAsync(i);
                    Chaos.Assert(ch.Count <= 1, "capacity exceeded");
                }

                ch.Complete();
            });
            var received = new List<int>();
            await foreach (int x in ch.ReadAllAsync())
            {
                received.Add(x);
            }

            await producer;
            Chaos.Assert(received.SequenceEqual([0, 1, 2, 3, 4]), $"order: {string.Join(",", received)}");
        }, new TestConfig { Iterations = 500, Strategy = Strategies.Random(5) });
        Assert.False(report.HasBugs, report.ToString());
    }

    [Fact]
    public void Receive_on_completed_empty_channel_throws()
    {
        TestReport report = TestEngine.Run(async () =>
        {
            var ch = new ControlledChannel<int>();
            ch.Complete();
            try
            {
                await ch.ReceiveAsync();
                Chaos.Fail("receive should have thrown");
            }
            catch (ChannelClosedException)
            {
            }
        }, new TestConfig { Iterations = 1 });
        Assert.False(report.HasBugs, report.ToString());
    }

    [Fact]
    public void SharedRegister_compare_exchange_is_atomic()
    {
        TestReport report = TestEngine.Run(async () =>
        {
            var reg = new SharedRegister<int>(0, "reg");
            async Task Increment()
            {
                while (true)
                {
                    int v = await reg.ReadAsync();
                    if (await reg.CompareExchangeAsync(v, v + 1))
                    {
                        return;
                    }
                }
            }

            await Task.WhenAll(ControlledTask.Run(Increment), ControlledTask.Run(Increment), ControlledTask.Run(Increment));
            Chaos.Assert(reg.Value == 3);
        }, new TestConfig { Iterations = 3000, Strategy = Strategies.Dfs(preemptionBound: 2) });
        Assert.False(report.HasBugs, report.ToString());
    }

    [Fact]
    public void Primitives_reset_when_reused_across_iterations()
    {
        // A static/shared lock left held by a buggy iteration must not poison the next iteration.
        var shared = new AsyncLock("shared");
        TestReport report = TestEngine.Run(async () =>
        {
            await shared.AcquireAsync(); // never released
        }, new TestConfig { Iterations = 20, Strategy = Strategies.Random(1) });
        Assert.False(report.HasBugs, report.ToString());
    }

    [Fact]
    public async Task Production_mode_falls_back_to_real_primitives()
    {
        Assert.False(Chaos.IsControlled);
        Assert.Equal(-1, Chaos.CurrentOperationId);

        var gate = new AsyncLock();
        int counter = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ControlledTask.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                using (await gate.AcquireAsync())
                {
                    int v = counter;
                    await ControlledTask.Yield();
                    counter = v + 1;
                }
            }
        })));
        Assert.Equal(800, counter);

        var ch = new ControlledChannel<int>(capacity: 2);
        Task producer = ControlledTask.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                await ch.SendAsync(i);
            }

            ch.Complete();
        });
        int sum = 0;
        await foreach (int x in ch.ReadAllAsync())
        {
            sum += x;
        }

        await producer;
        Assert.Equal(Enumerable.Range(0, 50).Sum(), sum);

        Assert.InRange(Chaos.RandomInt(10), 0, 9);
        await Chaos.Interleave(); // completes synchronously
        Assert.Throws<ChaosAssertionException>(() => Chaos.Assert(false, "production assert"));
    }
}
