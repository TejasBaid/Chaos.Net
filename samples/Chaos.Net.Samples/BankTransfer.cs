namespace ChaosNet.Samples;

/// <summary>
/// Sample 1: lost update in a bank. A transfer reads a balance, checks it, and writes it back. Two concurrent
/// transfers touching the same account can both read the old balance, and one write overwrites the other, so money is
/// created or destroyed.
/// </summary>
/// <remarks>
/// The fix acquires per-account <see cref="AsyncLock"/>s, always in ascending account order. Acquiring them in
/// argument order would trade the lost update for a lock-ordering deadlock, which Chaos.Net would also report.
/// </remarks>
public sealed class Bank
{
    private readonly SharedRegister<int>[] _balances;
    private readonly AsyncLock[] _locks;
    private readonly bool _buggy;

    /// <summary>Creates a bank.</summary>
    public Bank(int accounts, int initialBalance, bool buggy)
    {
        _balances = [.. Enumerable.Range(0, accounts).Select(i => new SharedRegister<int>(initialBalance, $"balance[{i}]"))];
        _locks = [.. Enumerable.Range(0, accounts).Select(i => new AsyncLock($"account[{i}]"))];
        _buggy = buggy;
    }

    /// <summary>Gets the sum of all balances.</summary>
    public int Total => _balances.Sum(b => b.Value);

    /// <summary>Gets the balance of an account.</summary>
    public int Balance(int account) => _balances[account].Value;

    /// <summary>Moves <paramref name="amount"/> between accounts if the source has sufficient funds.</summary>
    public async Task<bool> TransferAsync(int from, int to, int amount)
    {
        if (_buggy)
        {
            return await TransferCoreAsync(from, to, amount);
        }

        (int first, int second) = from < to ? (from, to) : (to, from);
        using (await _locks[first].AcquireAsync())
        using (await _locks[second].AcquireAsync())
        {
            return await TransferCoreAsync(from, to, amount);
        }
    }

    private async Task<bool> TransferCoreAsync(int from, int to, int amount)
    {
        int balance = await _balances[from].ReadAsync();
        if (balance < amount)
        {
            return false;
        }

        await _balances[from].WriteAsync(balance - amount);
        int target = await _balances[to].ReadAsync();
        await _balances[to].WriteAsync(target + amount);
        return true;
    }
}

/// <summary>Test harness for <see cref="Bank"/>.</summary>
public static class BankTransferSample
{
    /// <summary>Three concurrent transfers between two accounts; money must be conserved and no balance may go negative.</summary>
    public static async Task RunAsync(bool buggy)
    {
        var bank = new Bank(accounts: 2, initialBalance: 100, buggy);
        await Task.WhenAll(
            ControlledTask.Run(() => bank.TransferAsync(0, 1, 60), "transfer 0->1 $60"),
            ControlledTask.Run(() => bank.TransferAsync(1, 0, 30), "transfer 1->0 $30"),
            ControlledTask.Run(() => bank.TransferAsync(0, 1, 50), "transfer 0->1 $50"));
        Chaos.Assert(bank.Total == 200, $"money not conserved: total = {bank.Total} (balances {bank.Balance(0)}, {bank.Balance(1)})");
        Chaos.Assert(bank.Balance(0) >= 0 && bank.Balance(1) >= 0, $"negative balance: {bank.Balance(0)}, {bank.Balance(1)}");
    }

    /// <summary>The buggy system (entry point for the CLI).</summary>
    public static Task BuggyTest() => RunAsync(buggy: true);

    /// <summary>The fixed system (entry point for the CLI).</summary>
    public static Task FixedTest() => RunAsync(buggy: false);
}
