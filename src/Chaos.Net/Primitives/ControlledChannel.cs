using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ChaosNet.Runtime;

namespace ChaosNet;

/// <summary>
/// A FIFO channel (bounded or unbounded) for message passing between operations. Under test, sends and receives are
/// scheduling points and blocked senders/receivers are tracked. In production mode it wraps
/// <see cref="System.Threading.Channels.Channel{T}"/>.
/// </summary>
/// <typeparam name="T">The message type.</typeparam>
public sealed class ControlledChannel<T>
{
    private readonly Channel<T> _production;
    private readonly int? _capacity;
    private readonly Queue<T> _items = new();
    private readonly LinkedList<Receiver> _receivers = new();
    private readonly LinkedList<Sender> _senders = new();
    private long _epoch;
    private bool _completed;

    /// <summary>Creates a channel.</summary>
    /// <param name="capacity">Maximum buffered items, or <see langword="null"/> for an unbounded channel.</param>
    /// <param name="name">Name used in reports.</param>
    public ControlledChannel(int? capacity = null, string? name = null)
    {
        if (capacity is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        Name = name ?? "channel";
        _production = capacity is { } c
            ? Channel.CreateBounded<T>(new BoundedChannelOptions(c) { FullMode = BoundedChannelFullMode.Wait })
            : Channel.CreateUnbounded<T>();
    }

    /// <summary>Gets the channel name.</summary>
    public string Name { get; }

    /// <summary>Gets the number of buffered items.</summary>
    public int Count => ChaosRuntime.Current is { } rt ? (_epoch == rt.Epoch ? _items.Count : 0) : _production.Reader.Count;

    /// <summary>Sends an item, waiting while a bounded channel is full.</summary>
    /// <exception cref="ChannelClosedException">The channel has been completed.</exception>
    public async ValueTask SendAsync(T item, CancellationToken cancellationToken = default)
    {
        ChaosRuntime? rt = ChaosRuntime.ForApi();
        if (rt is null)
        {
            await _production.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            return;
        }

        Sync(rt);
        await new InterleaveAwaitable(rt, yieldInProduction: false);
        cancellationToken.ThrowIfCancellationRequested();
        if (TrySendCore(rt, item))
        {
            return;
        }

        var tcs = new TaskCompletionSource();
        WaitRecord wait = rt.BeginWait($"channel '{Name}' send (full, capacity {_capacity})", BlockKind.Resource);
        LinkedListNode<Sender> node = _senders.AddLast(new Sender(item, tcs, wait));
        using (cancellationToken.Register(() =>
        {
            if (node.List is not null)
            {
                _senders.Remove(node);
                rt.EndWait(wait);
                tcs.TrySetCanceled(cancellationToken);
            }
        }))
        {
            await tcs.Task;
        }
    }

    /// <summary>Tries to send without waiting and without a scheduling point.</summary>
    public bool TrySend(T item)
    {
        ChaosRuntime? rt = ChaosRuntime.ForApi();
        if (rt is null)
        {
            return _production.Writer.TryWrite(item);
        }

        Sync(rt);
        return TrySendCore(rt, item);
    }

    private bool TrySendCore(ChaosRuntime rt, T item)
    {
        if (_completed)
        {
            throw new ChannelClosedException();
        }

        if (_receivers.First is { } first)
        {
            _receivers.RemoveFirst();
            rt.EndWait(first.Value.Wait);
            if (rt.IsRecording)
            {
                rt.Note($"send '{Name}' <- {item} (handed to waiting receiver)");
            }

            first.Value.Completion.TrySetResult(item);
            return true;
        }

        if (_capacity is not { } cap || _items.Count < cap)
        {
            _items.Enqueue(item);
            if (rt.IsRecording)
            {
                rt.Note($"send '{Name}' <- {item}");
            }

            return true;
        }

        return false;
    }

    /// <summary>Receives an item, waiting while the channel is empty.</summary>
    /// <exception cref="ChannelClosedException">The channel has been completed and is empty.</exception>
    public async ValueTask<T> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ChaosRuntime? rt = ChaosRuntime.ForApi();
        if (rt is null)
        {
            return await _production.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        Sync(rt);
        await new InterleaveAwaitable(rt, yieldInProduction: false);
        cancellationToken.ThrowIfCancellationRequested();
        if (TryReceiveCore(rt, out T? item))
        {
            return item;
        }

        if (_completed)
        {
            throw new ChannelClosedException();
        }

        var tcs = new TaskCompletionSource<T>();
        WaitRecord wait = rt.BeginWait($"channel '{Name}' receive (empty)", BlockKind.Receive);
        LinkedListNode<Receiver> node = _receivers.AddLast(new Receiver(tcs, wait));
        using (cancellationToken.Register(() =>
        {
            if (node.List is not null)
            {
                _receivers.Remove(node);
                rt.EndWait(wait);
                tcs.TrySetCanceled(cancellationToken);
            }
        }))
        {
            T received = await tcs.Task;
            if (rt.IsRecording)
            {
                rt.Note($"receive '{Name}' -> {received}");
            }

            return received;
        }
    }

    /// <summary>Tries to receive without waiting and without a scheduling point.</summary>
    public bool TryReceive([System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T item)
    {
        ChaosRuntime? rt = ChaosRuntime.ForApi();
        if (rt is null)
        {
            return _production.Reader.TryRead(out item);
        }

        Sync(rt);
        return TryReceiveCore(rt, out item);
    }

    private bool TryReceiveCore(ChaosRuntime rt, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T item)
    {
        if (_items.Count == 0)
        {
            item = default;
            return false;
        }

        item = _items.Dequeue();
        if (rt.IsRecording)
        {
            rt.Note($"receive '{Name}' -> {item}");
        }

        if (_senders.First is { } first)
        {
            _senders.RemoveFirst();
            _items.Enqueue(first.Value.Item);
            rt.EndWait(first.Value.Wait);
            first.Value.Completion.TrySetResult();
        }

        return true;
    }

    /// <summary>Receives items until the channel is completed and drained.</summary>
    public async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChaosRuntime? rt = ChaosRuntime.ForApi();
        if (rt is null)
        {
            await foreach (T item in _production.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }

            yield break;
        }

        while (true)
        {
            T item;
            try
            {
                item = await ReceiveAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                yield break;
            }

            yield return item;
        }
    }

    /// <summary>Completes the channel: pending and future receives on an empty channel fail with <see cref="ChannelClosedException"/>.</summary>
    public void Complete()
    {
        ChaosRuntime? rt = ChaosRuntime.ForApi();
        if (rt is null)
        {
            _production.Writer.TryComplete();
            return;
        }

        Sync(rt);
        _completed = true;
        if (rt.IsRecording)
        {
            rt.Note($"complete '{Name}'");
        }

        while (_receivers.First is { } r)
        {
            _receivers.RemoveFirst();
            rt.EndWait(r.Value.Wait);
            r.Value.Completion.TrySetException(new ChannelClosedException());
        }

        while (_senders.First is { } s)
        {
            _senders.RemoveFirst();
            rt.EndWait(s.Value.Wait);
            s.Value.Completion.TrySetException(new ChannelClosedException());
        }
    }

    private void Sync(ChaosRuntime rt)
    {
        if (_epoch != rt.Epoch)
        {
            _epoch = rt.Epoch;
            _items.Clear();
            _receivers.Clear();
            _senders.Clear();
            _completed = false;
        }
    }

    private sealed record Receiver(TaskCompletionSource<T> Completion, WaitRecord Wait);

    private sealed record Sender(T Item, TaskCompletionSource Completion, WaitRecord Wait);
}
