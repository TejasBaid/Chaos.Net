using ChaosNet.Tracing;

namespace ChaosNet.Scheduling;

/// <summary>
/// Replays a recorded <see cref="ScheduleTrace"/> decision by decision, and reports nondeterminism if the program
/// does not offer the recorded choice.
/// </summary>
public sealed class ReplayStrategy : ISchedulingStrategy
{
    private readonly IReadOnlyList<Decision> _decisions;
    private int _position;

    /// <summary>Creates a replay strategy for <paramref name="trace"/>.</summary>
    public ReplayStrategy(ScheduleTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        Trace = trace;
        _decisions = trace.Decisions;
    }

    /// <summary>Gets the trace being replayed.</summary>
    public ScheduleTrace Trace { get; }

    /// <summary>Gets how many recorded decisions have been consumed.</summary>
    public int Consumed => _position;

    /// <inheritdoc />
    public string Name => "replay";

    /// <inheritdoc />
    public bool IsFair => true;

    /// <inheritdoc />
    public ulong? CurrentIterationSeed => Trace.Seed;

    /// <inheritdoc />
    public bool PrepareIteration(int iteration)
    {
        _position = 0;
        return iteration == 0;
    }

    /// <inheritdoc />
    public int NextOperation(IReadOnlyList<SchedulableOperation> enabled, int currentId, int step)
    {
        Decision d = Next(DecisionKind.Schedule, step);
        for (int i = 0; i < enabled.Count; i++)
        {
            if (enabled[i].Id == d.Value)
            {
                return d.Value;
            }
        }

        throw new NondeterminismException(
            $"Replay diverged at step {step}: the trace schedules {FormatOp(d.Value)} but the enabled operations are " +
            $"[{string.Join(", ", enabled.Select(e => FormatOp(e.Id)))}].");
    }

    /// <inheritdoc />
    public bool NextBoolean(int step) => Next(DecisionKind.Boolean, step).Value != 0;

    /// <inheritdoc />
    public int NextInteger(int maxValue, int step)
    {
        Decision d = Next(DecisionKind.Integer, step);
        if (d.Value >= maxValue)
        {
            throw new NondeterminismException($"Replay diverged at step {step}: recorded RandomInt value {d.Value} is out of range for max {maxValue}.");
        }

        return d.Value;
    }

    private Decision Next(DecisionKind kind, int step)
    {
        if (_position >= _decisions.Count)
        {
            throw new NondeterminismException($"Replay diverged at step {step}: the trace has ended ({_decisions.Count} decisions) but the program requested another {kind} decision.");
        }

        Decision d = _decisions[_position];
        if (d.Kind != kind)
        {
            throw new NondeterminismException($"Replay diverged at step {step}: the trace recorded a {d.Kind} decision but the program requested a {kind} decision.");
        }

        _position++;
        return d;
    }

    private static string FormatOp(int id) => id == SchedulableOperation.ClockId ? "clock" : $"op{id}";

    /// <inheritdoc />
    public void CompleteIteration(IterationOutcome outcome)
    {
    }

    /// <inheritdoc />
    public string? DescribeReproduction() => null;

    /// <inheritdoc />
    public override string ToString() => $"replay({Trace.Strategy})";
}
