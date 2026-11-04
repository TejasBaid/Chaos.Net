namespace ChaosNet.Tests;

/// <summary>Small programs shared by the scheduler tests.</summary>
internal static class TestPrograms
{
    /// <summary>Two workers, each with <paramref name="yields"/> scheduling points (so <c>yields + 1</c> atomic steps each).</summary>
    public static Func<Task> TwoWorkers(int yields) => async () =>
    {
        async Task Worker()
        {
            for (int i = 0; i < yields; i++)
            {
                await Chaos.Interleave();
            }
        }

        await Task.WhenAll(ControlledTask.Run(Worker, "A"), ControlledTask.Run(Worker, "B"));
    };

    /// <summary>Workers with the given numbers of scheduling points.</summary>
    public static Func<Task> Workers(params int[] yields) => async () =>
    {
        var tasks = new List<Task>();
        foreach (int y in yields)
        {
            tasks.Add(ControlledTask.Run(async () =>
            {
                for (int i = 0; i < y; i++)
                {
                    await Chaos.Interleave();
                }
            }));
        }

        await Task.WhenAll(tasks);
    };

    /// <summary>The classic lost update on a shared counter.</summary>
    public static async Task LostUpdate()
    {
        var counter = new SharedRegister<int>(0, "counter");
        async Task Increment()
        {
            int v = await counter.ReadAsync();
            await counter.WriteAsync(v + 1);
        }

        await Task.WhenAll(ControlledTask.Run(Increment, "inc1"), ControlledTask.Run(Increment, "inc2"));
        Chaos.Assert(counter.Value == 2, $"lost update: counter = {counter.Value}");
    }

    /// <summary>A nontrivial program with interleavings, nondeterministic choices and timers (no bug).</summary>
    public static async Task Busy()
    {
        var channel = new ControlledChannel<int>(capacity: 2, "ch");
        var gate = new AsyncLock("gate");
        var total = new SharedRegister<int>(0, "total");
        Task producer = ControlledTask.Run(async () =>
        {
            for (int i = 0; i < 6; i++)
            {
                if (Chaos.RandomBool())
                {
                    await ControlledTask.Delay(10);
                }

                await channel.SendAsync(Chaos.RandomInt(10));
            }

            channel.Complete();
        }, "producer");
        Task consumer = ControlledTask.Run(async () =>
        {
            await foreach (int item in channel.ReadAllAsync())
            {
                using (await gate.AcquireAsync())
                {
                    await total.UpdateAsync(t => t + item);
                }
            }
        }, "consumer");
        Task observer = ControlledTask.Run(async () =>
        {
            for (int i = 0; i < 4; i++)
            {
                using (await gate.AcquireAsync())
                {
                    _ = await total.ReadAsync();
                }
            }
        }, "observer");
        await Task.WhenAll(producer, consumer, observer);
    }

    public static long Binomial(int n, int k)
    {
        long r = 1;
        for (int i = 1; i <= k; i++)
        {
            r = r * (n - k + i) / i;
        }

        return r;
    }
}
