using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using ChaosNet.Runtime;

namespace ChaosNet;

/// <summary>
/// The Chaos.Net API for code under test: explicit scheduling points, controlled nondeterminism, assertions,
/// logging, virtual time and monitors.
/// </summary>
/// <remarks>
/// Every member works in two modes. Inside <see cref="TestEngine"/> the calls are controlled by the scheduler.
/// Outside a test ("production mode") they fall back to real behaviour: <see cref="Interleave"/> is a no-op,
/// <see cref="RandomInt"/> uses <see cref="Random.Shared"/>, <see cref="TimeProvider"/> is the system clock. The same
/// code can therefore ship and be tested.
/// </remarks>
public static class Chaos
{
    /// <summary>Gets whether the caller is running under a controlled test on the scheduler thread.</summary>
    public static bool IsControlled => ChaosRuntime.Current is not null;

    /// <summary>Gets the id of the controlled operation that is running, or -1 outside a controlled test.</summary>
    public static int CurrentOperationId => ChaosRuntime.Current is { } rt ? rt.CurrentOperation.Id : -1;

    /// <summary>
    /// An explicit scheduling point: <c>await Chaos.Interleave();</c> lets the scheduler run any other enabled
    /// operation before the caller continues. Put one between a read and a write of shared state to expose races.
    /// In production mode it completes synchronously and costs nothing.
    /// </summary>
    public static InterleaveAwaitable Interleave() => new(ChaosRuntime.ForApi(), yieldInProduction: false);

    /// <summary>Returns a controlled nondeterministic boolean (exhaustively explored by DFS, recorded for replay).</summary>
    public static bool RandomBool() => ChaosRuntime.ForApi() is { } rt ? rt.NextBoolean() : Random.Shared.Next(2) == 1;

    /// <summary>Returns a controlled nondeterministic integer in [0, <paramref name="maxExclusive"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxExclusive"/> is not positive.</exception>
    public static int RandomInt(int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxExclusive, 1);
        return ChaosRuntime.ForApi() is { } rt ? rt.NextInteger(maxExclusive) : Random.Shared.Next(maxExclusive);
    }

    /// <summary>
    /// Checks a safety property. On failure the bug is recorded immediately, so the iteration fails even if the
    /// exception is caught, and a <see cref="ChaosAssertionException"/> is thrown.
    /// </summary>
    /// <param name="condition">The property that must hold.</param>
    /// <param name="message">Optional description.</param>
    /// <param name="expression">Filled by the compiler with the source text of <paramref name="condition"/>.</param>
    public static void Assert([DoesNotReturnIf(false)] bool condition, string? message = null, [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (!condition)
        {
            Fail(message ?? $"Assertion failed: {expression}");
        }
    }

    /// <summary>Unconditionally fails the current iteration with an assertion failure.</summary>
    [DoesNotReturn]
    public static void Fail(string message)
    {
        ChaosRuntime.ForApi()?.RecordBug(BugKind.AssertionFailure, message, null);
        throw new ChaosAssertionException(message);
    }

    /// <summary>Adds an event to the current step of the bug timeline. Cheap when no timeline is being recorded.</summary>
    public static void Log(string message)
    {
        if (ChaosRuntime.Current is { IsRecording: true } rt)
        {
            rt.Note(message);
        }
    }

    /// <summary>Adds an interpolated event to the bug timeline; the string is only formatted while a timeline is recorded.</summary>
    public static void Log(ref LogInterpolatedStringHandler message)
    {
        if (message.Enabled && ChaosRuntime.Current is { } rt)
        {
            rt.Note(message.ToStringAndClear());
        }
    }

    /// <summary>
    /// Gets a <see cref="System.TimeProvider"/> that is virtual under test and the system clock otherwise. Code that
    /// takes a <see cref="System.TimeProvider"/> (the .NET 8+ abstraction used by <c>Task.Delay</c>,
    /// <c>PeriodicTimer</c>, <c>CancellationTokenSource</c>, ...) gets controlled time for free.
    /// </summary>
    public static TimeProvider TimeProvider => ChaosTimeProvider.Instance;

    /// <summary>Gets the current (virtual, under test) UTC time.</summary>
    public static DateTimeOffset UtcNow => ChaosTimeProvider.Instance.GetUtcNow();

    /// <summary>Gets the per-iteration instance of monitor <typeparamref name="T"/>, creating and registering it on first use.</summary>
    public static T Monitor<T>()
        where T : ChaosMonitor, new() =>
        ChaosRuntime.ForApi() is { } rt ? rt.GetMonitor<T>() : ProductionMonitor<T>.Instance;

    /// <summary>Registers <paramref name="monitor"/> with the current iteration so its liveness state is checked, and returns it.</summary>
    public static T RegisterMonitor<T>(T monitor)
        where T : ChaosMonitor
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return ChaosRuntime.ForApi() is { } rt ? rt.RegisterMonitor(monitor) : monitor;
    }

    private static class ProductionMonitor<T>
        where T : ChaosMonitor, new()
    {
        public static readonly T Instance = new();
    }
}

/// <summary>Awaitable returned by <see cref="Chaos.Interleave"/> and <see cref="ControlledTask.Yield"/>.</summary>
public readonly struct InterleaveAwaitable
{
    private readonly ChaosRuntime? _runtime;
    private readonly bool _yieldInProduction;

    internal InterleaveAwaitable(ChaosRuntime? runtime, bool yieldInProduction)
    {
        _runtime = runtime;
        _yieldInProduction = yieldInProduction;
    }

    /// <summary>Gets the awaiter.</summary>
    public Awaiter GetAwaiter() => new(_runtime, _yieldInProduction);

    /// <summary>The awaiter.</summary>
    public readonly struct Awaiter : ICriticalNotifyCompletion
    {
        private readonly ChaosRuntime? _runtime;
        private readonly bool _yieldInProduction;

        internal Awaiter(ChaosRuntime? runtime, bool yieldInProduction)
        {
            _runtime = runtime;
            _yieldInProduction = yieldInProduction;
        }

        /// <summary>Gets whether the await completes synchronously (only in production mode without yielding).</summary>
        public bool IsCompleted => _runtime is null && !_yieldInProduction;

        /// <inheritdoc />
        public void OnCompleted(Action continuation)
        {
            if (_runtime is null)
            {
                Task.Yield().GetAwaiter().OnCompleted(continuation);
                return;
            }

            ExecutionContext? ec = ExecutionContext.Capture();
            _runtime.YieldCurrent(ec is null ? continuation : () => ExecutionContext.Run(ec, static s => ((Action)s!)(), continuation));
        }

        /// <inheritdoc />
        public void UnsafeOnCompleted(Action continuation)
        {
            if (_runtime is null)
            {
                Task.Yield().GetAwaiter().UnsafeOnCompleted(continuation);
                return;
            }

            _runtime.YieldCurrent(continuation);
        }

        /// <summary>Ends the await.</summary>
        public void GetResult()
        {
        }
    }
}
