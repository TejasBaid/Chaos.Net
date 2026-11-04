namespace ChaosNet.Runtime;

/// <summary>
/// Virtual time. Time only moves when the scheduler picks the clock pseudo-operation, which jumps to the earliest
/// pending timer and fires it. A test that "sleeps" for an hour finishes instantly.
/// </summary>
internal sealed class VirtualClock
{
    /// <summary>The wall-clock instant that virtual time 0 maps to.</summary>
    public static readonly DateTimeOffset Epoch = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly PriorityQueue<TimerEntry, (long Due, long Sequence)> _queue = new();
    private long _sequence;
    private int _timerCount;

    public long NowTicks { get; private set; }

    public TimeSpan Elapsed => TimeSpan.FromTicks(NowTicks);

    public DateTimeOffset UtcNow => Epoch + Elapsed;

    public bool HasPendingTimers
    {
        get
        {
            Purge();
            return _queue.Count > 0;
        }
    }

    public VirtualTimer CreateTimer(string? name, Action<VirtualTimer> callback) =>
        new(this, name ?? $"timer#{++_timerCount}", callback);

    internal void Arm(VirtualTimer timer, long dueTicks)
    {
        timer.Generation++;
        _queue.Enqueue(new TimerEntry(timer, timer.Generation), (NowTicks + Math.Max(0, dueTicks), _sequence++));
    }

    internal static void Disarm(VirtualTimer timer) => timer.Generation++;

    public VirtualTimer PopNext()
    {
        Purge();
        if (!_queue.TryDequeue(out TimerEntry? entry, out (long Due, long Sequence) priority))
        {
            throw new InvalidOperationException("No pending timer.");
        }

        NowTicks = Math.Max(NowTicks, priority.Due);
        entry.Timer.Generation++; // the entry is consumed; a periodic timer re-arms itself in Fire()
        return entry.Timer;
    }

    private void Purge()
    {
        while (_queue.TryPeek(out TimerEntry? entry, out _) && entry.Generation != entry.Timer.Generation)
        {
            _queue.Dequeue();
        }
    }

    private sealed record TimerEntry(VirtualTimer Timer, int Generation);
}

/// <summary>A timer driven by <see cref="VirtualClock"/>. Also serves as the <see cref="ITimer"/> handed out by <see cref="ChaosTimeProvider"/>.</summary>
internal sealed class VirtualTimer : ITimer
{
    private readonly VirtualClock _clock;
    private readonly Action<VirtualTimer> _callback;
    private long _periodTicks;
    private bool _disposed;

    internal VirtualTimer(VirtualClock clock, string name, Action<VirtualTimer> callback)
    {
        _clock = clock;
        Name = name;
        _callback = callback;
    }

    public string Name { get; }

    internal int Generation { get; set; }

    public void Fire()
    {
        if (_disposed)
        {
            return;
        }

        if (_periodTicks > 0)
        {
            _clock.Arm(this, _periodTicks);
        }

        _callback(this);
    }

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        if (_disposed)
        {
            return false;
        }

        VirtualClock.Disarm(this);
        _periodTicks = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? 0 : period.Ticks;
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            _clock.Arm(this, Math.Max(0, dueTime.Ticks));
        }

        return true;
    }

    public void Dispose()
    {
        _disposed = true;
        VirtualClock.Disarm(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
