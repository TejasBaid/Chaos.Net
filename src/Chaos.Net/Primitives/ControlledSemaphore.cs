using ChaosNet.Runtime;

namespace ChaosNet;

/// <summary>
/// An asynchronous counting semaphore. Under test, <see cref="WaitAsync"/> is a scheduling point and blocked
/// waiters are tracked for deadlock diagnosis. In production mode it wraps <see cref="SemaphoreSlim"/>.
/// </summary>
public sealed class ControlledSemaphore
{
    private readonly SemaphoreSlim _production;
    private readonly LinkedList<(TaskCompletionSource Completion, WaitRecord Wait)> _waiters = new();
    private readonly int _initialCount;
    private readonly int _maxCount;
    private long _epoch;
    private int _count;

    /// <summary>Creates a semaphore.</summary>
    /// <param name="initialCount">Initial number of available slots.</param>
    /// <param name="maxCount">Maximum number of slots.</param>
    /// <param name="name">Name used in reports.</param>
    public ControlledSemaphore(int initialCount, int maxCount = int.MaxValue, string? name = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, Math.Max(1, initialCount));
        _initialCount = initialCount;
        _maxCount = maxCount;
        _count = initialCount;
        _production = new SemaphoreSlim(initialCount, maxCount);
        Name = name ?? "semaphore";
    }

    /// <summary>Gets the semaphore name.</summary>
    public string Name { get; }

    /// <summary>Gets the number of available slots.</summary>
    public int CurrentCount => ChaosRuntime.Current is { } rt ? (_epoch == rt.Epoch ? _count : _initialCount) : _production.CurrentCount;

    /// <summary>Waits for a slot.</summary>
    public async ValueTask WaitAsync(CancellationToken cancellationToken = default)
    {
        ChaosRuntime? rt = ChaosRuntime.ForApi();
        if (rt is null)
        {
            await _production.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        Sync(rt);
        await new InterleaveAwaitable(rt, yieldInProduction: false);
        cancellationToken.ThrowIfCancellationRequested();
        if (_count > 0)
        {
            _count--;
            if (rt.IsRecording)
            {
                rt.Note($"acquired semaphore '{Name}' ({_count} left)");
            }

            return;
        }

        var tcs = new TaskCompletionSource();
        WaitRecord wait = rt.BeginWait($"semaphore '{Name}' (0 slots)", BlockKind.Resource);
        LinkedListNode<(TaskCompletionSource, WaitRecord)> node = _waiters.AddLast((tcs, wait));
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
    }

    /// <summary>Releases <paramref name="releaseCount"/> slots.</summary>
    /// <exception cref="SemaphoreFullException">The release would exceed the maximum count.</exception>
    public void Release(int releaseCount = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(releaseCount, 1);
        ChaosRuntime? rt = ChaosRuntime.ForApi();
        if (rt is null)
        {
            _production.Release(releaseCount);
            return;
        }

        Sync(rt);
        if (rt.IsRecording)
        {
            rt.Note($"released semaphore '{Name}' x{releaseCount}");
        }

        for (int i = 0; i < releaseCount; i++)
        {
            if (_waiters.First is { } first)
            {
                _waiters.RemoveFirst();
                rt.EndWait(first.Value.Wait);
                first.Value.Completion.TrySetResult();
            }
            else
            {
                if (_count >= _maxCount)
                {
                    throw new SemaphoreFullException();
                }

                _count++;
            }
        }
    }

    private void Sync(ChaosRuntime rt)
    {
        if (_epoch != rt.Epoch)
        {
            _epoch = rt.Epoch;
            _count = _initialCount;
            _waiters.Clear();
        }
    }
}
