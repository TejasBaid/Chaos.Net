namespace ChaosNet.Samples;

/// <summary>
/// Sample 4: a producer/consumer deadlock. The producer updates shared statistics under a lock and, in the buggy
/// version, also sends to the bounded queue while still holding the lock. When the queue is full, the producer
/// waits for the consumer to make room while holding the lock. The consumer has just dequeued an item and waits for
/// the same lock to record it. Neither can proceed.
/// </summary>
/// <remarks>The fix never performs a potentially blocking operation (the bounded send) while holding the lock.</remarks>
public sealed class StatsPipeline(int capacity, bool buggy)
{
    private readonly ControlledChannel<int> _queue = new(capacity, "work-queue");
    private readonly AsyncLock _stats = new("stats");

    /// <summary>Gets how many items were produced.</summary>
    public int Produced { get; private set; }

    /// <summary>Gets the sum of consumed items.</summary>
    public int ConsumedSum { get; private set; }

    /// <summary>Produces items 1..<paramref name="count"/> and completes the queue.</summary>
    public async Task ProduceAsync(int count)
    {
        for (int i = 1; i <= count; i++)
        {
            if (buggy)
            {
                using (await _stats.AcquireAsync())
                {
                    Produced++;
                    await _queue.SendAsync(i); // BUG: blocking send while holding the stats lock
                }
            }
            else
            {
                using (await _stats.AcquireAsync())
                {
                    Produced++;
                }

                await _queue.SendAsync(i);
            }
        }

        _queue.Complete();
    }

    /// <summary>Consumes until the queue is completed.</summary>
    public async Task ConsumeAsync()
    {
        await foreach (int item in _queue.ReadAllAsync())
        {
            using (await _stats.AcquireAsync())
            {
                ConsumedSum += item;
            }
        }
    }
}

/// <summary>Test harness for <see cref="StatsPipeline"/>.</summary>
public static class ProducerConsumerSample
{
    /// <summary>One producer and one consumer over a queue of capacity 4; every produced item must be consumed.</summary>
    /// <remarks>The deadlock needs the producer to refill the whole queue between the consumer's dequeue and its lock attempt.</remarks>
    public static async Task RunAsync(bool buggy)
    {
        const int items = 10;
        var pipeline = new StatsPipeline(capacity: 4, buggy);
        await Task.WhenAll(
            ControlledTask.Run(() => pipeline.ProduceAsync(items), "producer"),
            ControlledTask.Run(pipeline.ConsumeAsync, "consumer"));
        Chaos.Assert(pipeline.ConsumedSum == items * (items + 1) / 2, $"consumed sum {pipeline.ConsumedSum}");
    }

    /// <summary>The buggy system (entry point for the CLI).</summary>
    public static Task BuggyTest() => RunAsync(buggy: true);

    /// <summary>The fixed system (entry point for the CLI).</summary>
    public static Task FixedTest() => RunAsync(buggy: false);
}
