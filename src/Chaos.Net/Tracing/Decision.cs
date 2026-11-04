namespace ChaosNet.Tracing;

/// <summary>The kind of a recorded scheduling decision.</summary>
public enum DecisionKind : byte
{
    /// <summary>The scheduler picked an operation (or the clock) to run.</summary>
    Schedule,

    /// <summary>A controlled boolean choice (<see cref="Chaos.RandomBool"/>).</summary>
    Boolean,

    /// <summary>A controlled integer choice (<see cref="Chaos.RandomInt"/>).</summary>
    Integer,
}

/// <summary>One decision in a schedule. A sequence of decisions fully determines a controlled execution.</summary>
/// <param name="Kind">What was decided.</param>
/// <param name="Value">The operation id (<see cref="Scheduling.SchedulableOperation.ClockId"/> for the clock), 0/1 for booleans, or the integer chosen.</param>
public readonly record struct Decision(DecisionKind Kind, int Value)
{
    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        DecisionKind.Schedule => Value == Scheduling.SchedulableOperation.ClockId ? "t" : Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DecisionKind.Boolean => Value != 0 ? "b1" : "b0",
        _ => "i" + Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
