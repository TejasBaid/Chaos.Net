using ChaosNet.Runtime;

namespace ChaosNet;

/// <summary>
/// An asynchronous mutual-exclusion lock. Under test, acquisition is a scheduling point, waiters are tracked for
/// deadlock diagnosis, and ownership is handed to waiters in FIFO order. In production mode it is a
/// <see cref="SemaphoreSlim"/>(1, 1).
/// </summary>
/// <example>
/// <code>
/// using (await accountLock.AcquireAsync())
/// {
///     balance -= amount;
/// }
/// </code>
/// </example>
public sealed class AsyncLock
{
    private readonly SemaphoreSlim _production = new(1, 1);
    private readonly LinkedList<Waiter> _waiters = new();
    private long _epoch;
    private bool _held;
    private Operation? _owner;

    /// <summary>Creates a lock.</summary>
    /// <param name="name">Name used in reports.</param>
    public AsyncLock(string? name = null) => Name = name ?? "lock";

    /// <summary>Gets the lock name.</summary>
    public string Name { get; }

    /// <summary>Gets whether the lock is held (controlled mode only; in production mode this is a best-effort snapshot).</summary>
    public bool IsHeld => ChaosRuntime.Current is { } rt ? _epoch == rt.Epoch && _held : _production.CurrentCount == 0;

    /// <summary>Acquires the lock. Dispose the returned releaser to release it.</summary>
    public async ValueTask<Releaser> AcquireAsync(CancellationToken cancellationToken = default)
    {
        ChaosRuntime? rt = ChaosRuntime.ForApi();
        if (rt is null)
        {
            await _production.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Releaser(this, null);
        }

        Sync(rt);
        await new InterleaveAwaitable(rt, yieldInProduction: false);
        cancellationToken.ThrowIfCancellationRequested();
        Operation me = rt.CurrentOperation;
        if (!_held)
        {
            _held = true;
            _owner = me;
            if (rt.IsRecording)
            {
                rt.Note($"acquired lock '{Name}'");
            }

            return new Releaser(this, rt);
        }

        var tcs = new TaskCompletionSource();
        WaitRecord wait = rt.BeginWait($"AsyncLock '{Name}' (held by {_owner})", BlockKind.Resource);
        LinkedListNode<Waiter> node = _waiters.AddLast(new Waiter(tcs, wait, me));
        using (cancellationToken.Register(() =>
        {
            if (node.List is not null)
            {
                _waiters.Remove(node);
                rt.EndWait(wait);
                tcs.TrySetCanceled(cancellationToken);
            }
        }))
        {
            await tcs.Task;
        }

        if (rt.IsRecording)
        {
            rt.Note($"acquired lock '{Name}' (after waiting)");
        }

        return new Releaser(this, rt);
    }

    private void Release(ChaosRuntime? rt)
    {
        if (rt is null)
        {
            _production.Release();
            return;
        }

        if (_epoch != rt.Epoch || !_held)
        {
            return;
        }

        if (_waiters.First is { } first)
        {
            _waiters.RemoveFirst();
            Waiter w = first.Value;
            _owner = w.Operation;
            rt.EndWait(w.Wait);
            if (rt.IsRecording)
            {
                rt.Note($"released lock '{Name}' -> handed to {w.Operation}");
            }

            w.Completion.TrySetResult();
        }
        else
        {
            _held = false;
            _owner = null;
            if (rt.IsRecording)
            {
                rt.Note($"released lock '{Name}'");
            }
        }
    }

    private void Sync(ChaosRuntime rt)
    {
        if (_epoch != rt.Epoch)
        {
            _epoch = rt.Epoch;
            _held = false;
            _owner = null;
            _waiters.Clear();
        }
    }

    private sealed record Waiter(TaskCompletionSource Completion, WaitRecord Wait, Operation Operation);

    /// <summary>Releases the lock when disposed.</summary>
    public readonly struct Releaser : IDisposable
    {
        private readonly AsyncLock? _lock;
        private readonly ChaosRuntime? _runtime;

        internal Releaser(AsyncLock @lock, ChaosRuntime? runtime)
        {
            _lock = @lock;
            _runtime = runtime;
        }

        /// <summary>Releases the lock.</summary>
        public void Dispose() => _lock?.Release(_runtime);
    }
}
