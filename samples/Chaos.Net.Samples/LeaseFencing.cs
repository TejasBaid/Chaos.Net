namespace ChaosNet.Samples;

/// <summary>A time-bounded lease, as handed out by a lock service (Azure Blob leases, etcd, ZooKeeper, Chubby).</summary>
/// <param name="Holder">The client holding the lease.</param>
/// <param name="Token">A monotonically increasing fencing token.</param>
/// <param name="Expiry">When the lease expires.</param>
public sealed record Lease(string Holder, long Token, DateTimeOffset Expiry);

/// <summary>A lease service. Leases expire after their TTL whether or not the holder is still working.</summary>
public sealed class LeaseService(TimeProvider time)
{
    private readonly Lock _gate = new();
    private string? _holder;
    private DateTimeOffset _expiry;
    private long _token;

    /// <summary>Tries to acquire the lease for <paramref name="ttl"/>.</summary>
    public async Task<Lease?> TryAcquireAsync(string client, TimeSpan ttl)
    {
        await ControlledTask.Yield(); // RPC to the lease service
        lock (_gate)
        {
            DateTimeOffset now = time.GetUtcNow();
            if (_holder is not null && _holder != client && now < _expiry)
            {
                return null;
            }

            _holder = client;
            _expiry = now + ttl;
            _token++;
            Chaos.Log($"{client} acquires lease #{_token} until t+{(_expiry - now).TotalMilliseconds:0}ms");
            return new Lease(client, _token, _expiry);
        }
    }

    /// <summary>Releases the lease if it is still held by <paramref name="lease"/>.</summary>
    public async Task ReleaseAsync(Lease lease)
    {
        await ControlledTask.Yield();
        lock (_gate)
        {
            if (_holder == lease.Holder && _token == lease.Token)
            {
                _holder = null;
            }
        }
    }
}

/// <summary>
/// The storage the lease protects. The fixed version implements <em>fencing</em>: every access carries the lease's
/// token, the store remembers the highest token it has seen, and it rejects older tokens. A client whose lease
/// expired while it was paused can no longer touch the data.
/// </summary>
public sealed class FencedStore(bool fencing)
{
    private readonly Lock _gate = new();
    private int _value;
    private long _highestToken;

    /// <summary>Gets the stored value without a scheduling point.</summary>
    public int Value
    {
        get
        {
            lock (_gate)
            {
                return _value;
            }
        }
    }

    /// <summary>Reads the value under <paramref name="token"/>; returns <see langword="null"/> if the token is stale.</summary>
    public async Task<int?> ReadAsync(long token)
    {
        await ControlledTask.Yield();
        lock (_gate)
        {
            if (fencing && !Admit(token))
            {
                return null;
            }

            return _value;
        }
    }

    /// <summary>Writes the value under <paramref name="token"/>; returns <see langword="false"/> if the token is stale.</summary>
    public async Task<bool> WriteAsync(int value, long token)
    {
        await ControlledTask.Yield();
        lock (_gate)
        {
            if (fencing && !Admit(token))
            {
                Chaos.Log($"store rejects write {value} with stale token #{token}");
                return false;
            }

            _value = value;
            Chaos.Log($"store accepts write {value} (token #{token})");
            return true;
        }
    }

    private bool Admit(long token)
    {
        if (token < _highestToken)
        {
            return false;
        }

        _highestToken = token;
        return true;
    }
}

/// <summary>
/// Sample 6: a distributed lock without fencing (Martin Kleppmann, "How to do distributed locking", 2016). Two
/// clients increment a counter while holding a lease. If a client pauses (a GC pause, a page fault, a slow disk)
/// for longer than the lease TTL after reading, the lease expires and the other client acquires it, reads, and
/// writes. Then the paused client wakes up and overwrites that write with its stale value.
/// </summary>
/// <remarks>
/// Stress testing practically never finds this: the work takes microseconds, the TTL is 100 ms, and no test
/// machine pauses a thread for 95 ms on demand. Under Chaos.Net, time is virtual and
/// <see cref="TimerPolicy.Nondeterministic"/> lets the scheduler advance the clock while a ready operation waits
/// to run. A "pause longer than the TTL" is an ordinary scheduling choice.
/// </remarks>
public static class LeaseFencingSample
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan WorkTime = TimeSpan.FromMilliseconds(5);

    /// <summary>Two clients each increment the counter once under the lease. Every successful increment must be reflected in the counter.</summary>
    public static async Task RunAsync(bool buggy)
    {
        var leases = new LeaseService(Chaos.TimeProvider);
        var store = new FencedStore(fencing: !buggy);
        bool[] committed = await Task.WhenAll(
            ControlledTask.Run(() => IncrementAsync("A", leases, store), "client A"),
            ControlledTask.Run(() => IncrementAsync("B", leases, store), "client B"));
        int successes = committed.Count(c => c);
        Chaos.Assert(store.Value == successes, $"lost update: {successes} clients committed an increment but the counter is {store.Value}");
    }

    private static async Task<bool> IncrementAsync(string client, LeaseService leases, FencedStore store)
    {
        Lease? lease = null;
        for (int attempt = 0; attempt < 10 && lease is null; attempt++)
        {
            lease = await leases.TryAcquireAsync(client, Ttl);
            if (lease is null)
            {
                await ControlledTask.Delay(RetryDelay);
            }
        }

        if (lease is null || await store.ReadAsync(lease.Token) is not { } value)
        {
            return false;
        }

        await ControlledTask.Delay(WorkTime); // compute the new value; a pause here is where the danger lies
        bool ok = await store.WriteAsync(value + 1, lease.Token);
        await leases.ReleaseAsync(lease);
        return ok;
    }

    /// <summary>The buggy system (entry point for the CLI).</summary>
    public static Task BuggyTest() => RunAsync(buggy: true);

    /// <summary>The fixed system (entry point for the CLI).</summary>
    public static Task FixedTest() => RunAsync(buggy: false);
}
