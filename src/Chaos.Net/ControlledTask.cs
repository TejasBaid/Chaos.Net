using ChaosNet.Runtime;

namespace ChaosNet;

/// <summary>
/// Controlled replacements for <see cref="Task.Run(Func{Task})"/>, <see cref="Task.Delay(TimeSpan)"/> and
/// <see cref="Task.Yield"/>. Under test, each <see cref="Run(Func{Task}, string?)"/> creates a new controlled
/// operation and delays run on virtual time. In production mode they forward to the real TPL.
/// </summary>
public static class ControlledTask
{
    /// <summary>Runs <paramref name="work"/> concurrently as a new controlled operation.</summary>
    /// <param name="work">The asynchronous work.</param>
    /// <param name="label">A label for reports and timelines.</param>
    public static Task Run(Func<Task> work, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        return ChaosRuntime.ForApi() is { } rt ? rt.Spawn(work, label) : Task.Run(work);
    }

    /// <summary>Runs <paramref name="work"/> concurrently as a new controlled operation and returns its result.</summary>
    public static Task<T> Run<T>(Func<Task<T>> work, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        return ChaosRuntime.ForApi() is { } rt ? rt.Spawn(work, label) : Task.Run(work);
    }

    /// <summary>Runs synchronous <paramref name="work"/> concurrently as a new controlled operation.</summary>
    public static Task Run(Action work, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        return ChaosRuntime.ForApi() is { } rt
            ? rt.Spawn(() =>
            {
                work();
                return Task.CompletedTask;
            }, label)
            : Task.Run(work);
    }

    /// <summary>Waits for <paramref name="delay"/> of (virtual, under test) time.</summary>
    public static Task Delay(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (ChaosRuntime.ForApi() is not { } rt)
        {
            return Task.Delay(delay, cancellationToken);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var tcs = new TaskCompletionSource();
        string name = rt.IsRecording ? $"Delay({delay.TotalMilliseconds:0.###}ms) of {rt.CurrentOperation}" : "Delay";
        VirtualTimer timer = rt.Clock.CreateTimer(name, _ => tcs.TrySetResult());
        timer.Change(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
        if (cancellationToken.CanBeCanceled)
        {
            CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                timer.Dispose();
                tcs.TrySetCanceled(cancellationToken);
            });
            tcs.Task.ContinueWith(static (_, r) => ((CancellationTokenRegistration)r!).Dispose(), registration,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        return tcs.Task;
    }

    /// <summary>Waits for <paramref name="millisecondsDelay"/> milliseconds of (virtual, under test) time.</summary>
    public static Task Delay(int millisecondsDelay, CancellationToken cancellationToken = default) =>
        Delay(TimeSpan.FromMilliseconds(millisecondsDelay), cancellationToken);

    /// <summary>
    /// A scheduling point that, unlike <see cref="Chaos.Interleave"/>, really yields in production mode
    /// (like <see cref="Task.Yield"/>). Use it to model a step that goes asynchronous in production, such as an I/O call.
    /// </summary>
    public static InterleaveAwaitable Yield() => new(ChaosRuntime.ForApi(), yieldInProduction: true);
}
