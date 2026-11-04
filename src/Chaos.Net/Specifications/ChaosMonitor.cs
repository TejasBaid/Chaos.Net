using ChaosNet.Runtime;

namespace ChaosNet;

/// <summary>
/// A specification monitor: an object that observes events from the system under test and checks safety
/// (<see cref="Assert"/>) and liveness (<see cref="Hot"/>/<see cref="Cold"/>) properties.
/// </summary>
/// <remarks>
/// <para>
/// Liveness follows the hot/cold state idea from P and Coyote. A monitor becomes <em>hot</em> when the system owes
/// progress (a request is pending, a replica is behind) and <em>cold</em> when the obligation is discharged. If the
/// monitor is hot at the end of an execution, at the step bound, or for longer than
/// <see cref="TestConfig.LivenessTemperatureThreshold"/> consecutive steps, Chaos.Net reports a
/// <see cref="BugKind.LivenessViolation"/>.
/// </para>
/// <para>
/// Obtain monitors with <see cref="Chaos.Monitor{T}"/> (one instance per iteration) or
/// <see cref="Chaos.RegisterMonitor{T}(T)"/>. Outside a controlled test, hot/cold tracking is a no-op and
/// assertions throw <see cref="ChaosAssertionException"/>.
/// </para>
/// </remarks>
public abstract class ChaosMonitor
{
    /// <summary>Gets the monitor name used in reports. Defaults to the type name.</summary>
    public virtual string Name => GetType().Name;

    /// <summary>Gets whether the monitor is currently hot.</summary>
    public bool IsHot { get; private set; }

    /// <summary>Gets why the monitor is hot, if it is.</summary>
    public string? HotReason { get; private set; }

    /// <summary>Gets the step at which the monitor last became hot.</summary>
    public int HotSinceStep { get; private set; }

    internal ChaosRuntime? Runtime { get; private set; }

    internal void Attach(ChaosRuntime runtime)
    {
        Runtime = runtime;
        IsHot = false;
        HotReason = null;
    }

    /// <summary>Marks the monitor hot: the system owes progress until <see cref="Cold"/> is called.</summary>
    /// <param name="reason">Why progress is owed; shown in the bug report.</param>
    protected void Hot(string reason)
    {
        if (Runtime is null || IsHot)
        {
            return;
        }

        IsHot = true;
        HotReason = reason;
        HotSinceStep = Runtime.Step;
        if (Runtime.IsRecording)
        {
            Runtime.Note($"monitor {Name} -> HOT ({reason})");
        }
    }

    /// <summary>Marks the monitor cold: no progress is owed.</summary>
    protected void Cold()
    {
        if (Runtime is null || !IsHot)
        {
            return;
        }

        IsHot = false;
        HotReason = null;
        if (Runtime.IsRecording)
        {
            Runtime.Note($"monitor {Name} -> cold");
        }
    }

    /// <summary>Checks a safety property.</summary>
    protected void Assert(bool condition, string message)
    {
        if (!condition)
        {
            Chaos.Fail($"[{Name}] {message}");
        }
    }
}
