namespace ChaosNet.Samples;

/// <summary>
/// Sample 5, a depth-3 bug: a transactional store with fuzzy checkpoints. Transactions write their cells one at a
/// time. A checkpointer scans the store (optimistically, retrying if any write lands during the scan) and then rolls
/// in-flight transactions back out of the snapshot using their before-images, so that the checkpoint is
/// transaction-consistent (all or nothing per transaction).
/// </summary>
/// <remarks>
/// <para>
/// The bug: in-flight transactions are tracked as a stack designed for <em>nested</em> transactions (begin pushes,
/// commit pops), and the checkpointer only rolls back the innermost one. That is correct for one transaction, and
/// for two transactions that do not overlap. It is wrong only when two independent transactions are <em>both</em>
/// in flight and a whole checkpoint scan completes between their writes. The older transaction's partial writes
/// then leak into the checkpoint.
/// </para>
/// <para>
/// Manifesting it needs T1 preempted mid-write, T2 preempted before it commits, and the checkpointer running an
/// entire uninterrupted scan before either resumes. That is bug depth 3. PCT with depth 3 places one priority change
/// point inside each transaction with a probability that does not depend on the scan length. Random walk must
/// instead get lucky on every step of the scan, which is exponentially unlikely in the scan length. PCT with depth 2
/// cannot find it, because the second transaction would run to completion and pop itself.
/// </para>
/// <para>
/// Every step between two scheduling points runs under a plain <c>lock</c>, so production execution has exactly the
/// atomicity the controlled model assumes. A lock body cannot contain <c>await</c>, so under test it never blocks.
/// </para>
/// </remarks>
public sealed class CheckpointedStore(int cells, bool buggy)
{
    private readonly Lock _gate = new();
    private readonly int[] _data = new int[cells];
    private Transaction? _innermost;
    private int _version;

    /// <summary>Runs a transaction that writes <paramref name="id"/> into <paramref name="cellsToWrite"/>.</summary>
    public async Task RunTransactionAsync(int id, int[] cellsToWrite)
    {
        var txn = new Transaction(id);
        lock (_gate)
        {
            txn.Outer = _innermost;
            _innermost = txn;
        }

        Chaos.Log($"T{id} begins");
        foreach (int cell in cellsToWrite)
        {
            await Chaos.Interleave();
            lock (_gate)
            {
                txn.Undo.Add((cell, _data[cell]));
                _data[cell] = id;
                _version++;
            }

            Chaos.Log($"T{id} writes cell {cell}");
        }

        await Chaos.Interleave();
        lock (_gate)
        {
            Unlink(txn);
        }

        Chaos.Log($"T{id} commits");
    }

    /// <summary>Takes a transaction-consistent snapshot, retrying up to <paramref name="attempts"/> times.</summary>
    public async Task<int[]?> CheckpointAsync(int attempts)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            int version;
            List<Transaction> inFlight = [];
            lock (_gate)
            {
                version = _version;
                if (buggy)
                {
                    if (_innermost is { } top)
                    {
                        inFlight.Add(top); // BUG: only the innermost transaction is rolled back
                    }
                }
                else
                {
                    for (Transaction? t = _innermost; t is not null; t = t.Outer)
                    {
                        inFlight.Add(t);
                    }
                }
            }

            Chaos.Log($"checkpoint attempt {attempt + 1}: scanning, will roll back [{string.Join(",", inFlight.Select(t => "T" + t.Id))}]");
            var snapshot = new int[_data.Length];
            for (int i = 0; i < snapshot.Length; i++)
            {
                await Chaos.Interleave();
                lock (_gate)
                {
                    snapshot[i] = _data[i];
                }
            }

            lock (_gate)
            {
                if (_version != version)
                {
                    Chaos.Log("checkpoint scan invalidated by a concurrent write; retrying");
                    continue; // a write landed during the scan: retry
                }

                foreach (Transaction t in inFlight)
                {
                    foreach ((int cell, int before) in t.Undo)
                    {
                        snapshot[cell] = before;
                    }
                }
            }

            Chaos.Log($"checkpoint taken: [{string.Join(",", snapshot)}]");
            return snapshot;
        }

        return null;
    }

    private void Unlink(Transaction txn)
    {
        if (_innermost == txn)
        {
            _innermost = txn.Outer;
            return;
        }

        for (Transaction? t = _innermost; t is not null; t = t.Outer)
        {
            if (t.Outer == txn)
            {
                t.Outer = txn.Outer;
                return;
            }
        }
    }

    private sealed class Transaction(int id)
    {
        public int Id { get; } = id;

        public Transaction? Outer { get; set; }

        public List<(int Cell, int Before)> Undo { get; } = [];
    }
}

/// <summary>Test harness for <see cref="CheckpointedStore"/>.</summary>
public static class CheckpointSample
{
    /// <summary>Default number of cells written by each transaction.</summary>
    public const int DefaultCellsPerTransaction = 6;

    /// <summary>Two transactions over disjoint cells and one checkpointer; every checkpoint must be all-or-nothing per transaction.</summary>
    public static Task RunAsync(bool buggy) => RunAsync(buggy, DefaultCellsPerTransaction);

    /// <summary>As <see cref="RunAsync(bool)"/> with <paramref name="cellsPerTransaction"/> cells per transaction (the checkpoint scans twice as many).</summary>
    public static async Task RunAsync(bool buggy, int cellsPerTransaction)
    {
        int n = cellsPerTransaction;
        var store = new CheckpointedStore(2 * n, buggy);
        int[] t1Cells = [.. Enumerable.Range(0, n)];
        int[] t2Cells = [.. Enumerable.Range(n, n)];
        Task t1 = ControlledTask.Run(() => store.RunTransactionAsync(1, t1Cells), "T1");
        Task t2 = ControlledTask.Run(() => store.RunTransactionAsync(2, t2Cells), "T2");
        Task<int[]?> checkpoint = ControlledTask.Run(() => store.CheckpointAsync(attempts: 20), "checkpointer");
        await Task.WhenAll(t1, t2, checkpoint);

        if (await checkpoint is { } snapshot)
        {
            foreach (int id in new[] { 1, 2 })
            {
                int written = snapshot.Count(v => v == id);
                Chaos.Assert(written == 0 || written == n,
                    $"checkpoint is not transaction-consistent: it contains {written}/{n} writes of T{id}: [{string.Join(",", snapshot)}]");
            }
        }
    }

    /// <summary>The buggy system (entry point for the CLI).</summary>
    public static Task BuggyTest() => RunAsync(buggy: true);

    /// <summary>The fixed system (entry point for the CLI).</summary>
    public static Task FixedTest() => RunAsync(buggy: false);
}
