using System.Diagnostics;
using ChaosNet.Runtime;

namespace ChaosNet;

/// <summary>
/// A <see cref="TimeProvider"/> that reads and schedules on virtual time when called from a controlled test and
/// delegates to <see cref="TimeProvider.System"/> otherwise. Because it resolves the mode on every call, a single
/// shared instance can be registered in dependency injection and used in both production and tests.
/// </summary>
public sealed class ChaosTimeProvider : TimeProvider
{
    private ChaosTimeProvider()
    {
    }

    /// <summary>Gets the shared instance.</summary>
    public static ChaosTimeProvider Instance { get; } = new();

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => ChaosRuntime.Current is null ? TimeZoneInfo.Local : TimeZoneInfo.Utc;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => ChaosRuntime.ForApi() is { } rt ? rt.Clock.UtcNow : System.GetUtcNow();

    /// <inheritdoc />
    public override long GetTimestamp() => ChaosRuntime.ForApi() is { } rt
        ? (long)((Int128)rt.Clock.NowTicks * Stopwatch.Frequency / TimeSpan.TicksPerSecond)
        : Stopwatch.GetTimestamp();

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (ChaosRuntime.ForApi() is not { } rt)
        {
            return System.CreateTimer(callback, state, dueTime, period);
        }

        ExecutionContext? ec = ExecutionContext.Capture();
        VirtualTimer timer = rt.Clock.CreateTimer(null, _ =>
        {
            if (ec is null)
            {
                callback(state);
            }
            else
            {
                ExecutionContext.Run(ec, static s =>
                {
                    var (cb, st) = ((TimerCallback, object?))s!;
                    cb(st);
                }, (callback, state));
            }
        });
        timer.Change(dueTime, period);
        return timer;
    }
}
