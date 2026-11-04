namespace ChaosNet.Samples;

/// <summary>Describes a sample system for tests, the benchmark harness and the CLI.</summary>
/// <param name="Name">Short name.</param>
/// <param name="Description">What the bug is.</param>
/// <param name="Run">Runs the system: <c>Run(buggy)</c>.</param>
/// <param name="ExpectedBug">The bug class Chaos.Net reports for the buggy version.</param>
/// <param name="PctDepth">The PCT depth the bug needs.</param>
/// <param name="MaxSteps">A sensible step bound.</param>
public sealed record SampleInfo(string Name, string Description, Func<bool, Task> Run, BugKind ExpectedBug, int PctDepth, int MaxSteps = 10_000)
{
    /// <summary>The buggy version.</summary>
    public Task Buggy() => Run(true);

    /// <summary>The fixed version.</summary>
    public Task Fixed() => Run(false);
}

/// <summary>All sample systems.</summary>
public static class SampleCatalog
{
    /// <summary>Gets all samples.</summary>
    public static IReadOnlyList<SampleInfo> All { get; } =
    [
        new("bank-transfer", "Lost update: concurrent read-check-write transfers create or destroy money", BankTransferSample.RunAsync, BugKind.AssertionFailure, PctDepth: 2),
        new("async-cache", "Check-then-act: a reader populates the cache with a value invalidated mid-flight", AsyncCacheSample.RunAsync, BugKind.AssertionFailure, PctDepth: 2),
        new("leader-election", "Raft-style election over a lossy/duplicating network elects two leaders in one term", LeaderElectionSample.RunAsync, BugKind.AssertionFailure, PctDepth: 2),
        new("producer-consumer", "Producer sends to a full bounded queue while holding the lock the consumer needs", ProducerConsumerSample.RunAsync, BugKind.Deadlock, PctDepth: 2),
        new("lease-fencing", "A client paused past its lease TTL overwrites the next holder's write (no fencing tokens)", LeaseFencingSample.RunAsync, BugKind.AssertionFailure, PctDepth: 2),
        new("checkpoint-depth3", "Fuzzy checkpoint leaks a partial transaction only when two transactions are both mid-flight", CheckpointSample.RunAsync, BugKind.AssertionFailure, PctDepth: 3),
    ];

    /// <summary>Finds a sample by name.</summary>
    public static SampleInfo Get(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown sample '{name}'. Known: {string.Join(", ", All.Select(s => s.Name))}", nameof(name));
}
