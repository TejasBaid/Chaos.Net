using System.Collections.Concurrent;

namespace ChaosNet.Samples;

/// <summary>
/// A tiny key-value "database" behind a network. A read is evaluated at the server and the response travels back
/// later, so other operations can run between the moment a value is read and the moment the client sees it. The
/// latency is modelled with <see cref="ControlledTask.Yield"/>, which is a scheduling point under test and a real
/// <see cref="Task.Yield"/> in production.
/// </summary>
public sealed class Database
{
    private readonly ConcurrentDictionary<string, SharedRegister<int>> _rows = new();

    /// <summary>Reads a row (0 if missing).</summary>
    public async ValueTask<int> ReadAsync(string key)
    {
        int value = await Row(key).ReadAsync(); // request reaches the server, row is read
        await ControlledTask.Yield();           // response travels back
        return value;
    }

    /// <summary>Writes a row.</summary>
    public async ValueTask WriteAsync(string key, int value)
    {
        await Row(key).WriteAsync(value);       // request reaches the server, row is written
        await ControlledTask.Yield();           // acknowledgement travels back
    }

    /// <summary>Reads a row without a scheduling point.</summary>
    public int Peek(string key) => Row(key).Value;

    private SharedRegister<int> Row(string key) => _rows.GetOrAdd(key, k => new SharedRegister<int>(0, $"db[{k}]"));
}

/// <summary>
/// Sample 2: a read-through cache with a check-then-act race. On a miss, <c>GetAsync</c> reads the database and then
/// populates the cache. If a writer updates the database and invalidates the key between the read and the populate,
/// the reader installs the stale value and the cache serves it until the next invalidation, possibly forever.
/// This is the race behind memcache "leases".
/// </summary>
/// <remarks>
/// The fix gives each key a generation. Invalidation bumps it, and a reader may install a value only with a
/// compare-and-swap that proves no invalidation happened since its miss. The cache never holds a value older than the
/// last invalidation.
/// </remarks>
public sealed class ReadThroughCache(Database db, bool buggy)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>Reads through the cache.</summary>
    public async Task<int> GetAsync(string key)
    {
        _entries.TryGetValue(key, out Entry? observed);
        if (observed is { Filled: true })
        {
            Chaos.Log($"cache hit {key} = {observed.Value}");
            return observed.Value;
        }

        int value = await db.ReadAsync(key); // the database read; other operations may run here
        if (buggy)
        {
            _entries[key] = new Entry(true, value, observed?.Generation ?? 0); // BUG: unconditional populate
            Chaos.Log($"cache fill {key} = {value}");
            return value;
        }

        var filled = new Entry(true, value, observed?.Generation ?? 0);
        bool installed = observed is null ? _entries.TryAdd(key, filled) : _entries.TryUpdate(key, filled, observed);
        Chaos.Log(installed ? $"cache fill {key} = {value}" : $"cache fill {key} skipped (invalidated meanwhile)");
        return value;
    }

    /// <summary>Writes the database and invalidates the cached value.</summary>
    public async Task UpdateAsync(string key, int value)
    {
        await db.WriteAsync(key, value);
        while (true)
        {
            _entries.TryGetValue(key, out Entry? current);
            var invalidated = new Entry(false, 0, (current?.Generation ?? 0) + 1);
            if (current is null ? _entries.TryAdd(key, invalidated) : _entries.TryUpdate(key, invalidated, current))
            {
                Chaos.Log($"cache invalidate {key}");
                return;
            }
        }
    }

    /// <summary>Returns the cached value, if any.</summary>
    public bool TryPeek(string key, out int value)
    {
        if (_entries.TryGetValue(key, out Entry? e) && e.Filled)
        {
            value = e.Value;
            return true;
        }

        value = 0;
        return false;
    }

    private sealed record Entry(bool Filled, int Value, long Generation);
}

/// <summary>Test harness for <see cref="ReadThroughCache"/>.</summary>
public static class AsyncCacheSample
{
    /// <summary>A reader and a writer race on one key. Afterwards a cached value, if present, must equal the database.</summary>
    public static async Task RunAsync(bool buggy)
    {
        var db = new Database();
        await db.WriteAsync("user:42", 1);
        var cache = new ReadThroughCache(db, buggy);
        await Task.WhenAll(
            ControlledTask.Run(() => cache.GetAsync("user:42"), "reader"),
            ControlledTask.Run(() => cache.UpdateAsync("user:42", 2), "writer"));

        int fresh = db.Peek("user:42");
        if (cache.TryPeek("user:42", out int cached))
        {
            Chaos.Assert(cached == fresh, $"stale cache: cache holds {cached} but the database holds {fresh}");
        }
    }

    /// <summary>The buggy system (entry point for the CLI).</summary>
    public static Task BuggyTest() => RunAsync(buggy: true);

    /// <summary>The fixed system (entry point for the CLI).</summary>
    public static Task FixedTest() => RunAsync(buggy: false);
}
