namespace ChaosNet.Scheduling;

/// <summary>
/// An operation that the scheduler may run at the current scheduling point.
/// </summary>
/// <param name="Id">The operation id. Ids are assigned in creation order, so they are stable across replays. The
/// virtual clock uses the reserved id <see cref="ClockId"/>.</param>
/// <param name="Label">A human-readable label (for example the label passed to <c>ControlledTask.Run</c>).</param>
public readonly record struct SchedulableOperation(int Id, string Label)
{
    /// <summary>The id of the pseudo-operation that advances virtual time and fires the next timer.</summary>
    public const int ClockId = -1;

    /// <summary>Gets whether this choice fires the next virtual timer rather than running an operation.</summary>
    public bool IsClock => Id == ClockId;
}

/// <summary>Summary of a finished iteration, passed back to the strategy so it can adapt (PCT) or backtrack (DFS).</summary>
/// <param name="Iteration">Zero-based iteration index.</param>
/// <param name="Steps">Number of scheduling steps taken.</param>
/// <param name="BugFound">Whether the iteration found a bug.</param>
public readonly record struct IterationOutcome(int Iteration, int Steps, bool BugFound);

/// <summary>
/// A pluggable exploration strategy. At every scheduling point the runtime hands the strategy the list of enabled
/// operations and the strategy picks one. Controlled nondeterminism (<see cref="Chaos.RandomBool"/>,
/// <see cref="Chaos.RandomInt"/>) is also resolved by the strategy, so a strategy fully determines an execution.
/// </summary>
/// <remarks>
/// Implementations must be deterministic functions of their seed and the sequence of calls they receive; that is
/// what makes every execution replayable. Strategies are stateful and are not thread-safe.
/// </remarks>
public interface ISchedulingStrategy
{
    /// <summary>Gets a short name, for example <c>pct</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Gets whether the strategy is fair, that is, whether every continuously enabled operation is eventually
    /// scheduled. Unfair strategies (PCT, DFS) are switched to a seeded random fallback after
    /// <see cref="TestConfig.MaxUnfairSteps"/> steps so that spin loops cannot masquerade as livelocks.
    /// </summary>
    bool IsFair { get; }

    /// <summary>Prepares the strategy for an iteration. Returning <see langword="false"/> ends the search (for example when DFS has exhausted the state space).</summary>
    /// <param name="iteration">Zero-based iteration index. Iteration 0 resets any state learned in a previous run.</param>
    bool PrepareIteration(int iteration);

    /// <summary>Picks the next operation to run.</summary>
    /// <param name="enabled">The enabled operations, ordered by id, with the clock (if enabled) last. Never empty.</param>
    /// <param name="currentId">The id of the operation that ran the previous step, or <see cref="SchedulableOperation.ClockId"/>/-2 at the start.</param>
    /// <param name="step">The zero-based index of this step.</param>
    /// <returns>The <see cref="SchedulableOperation.Id"/> of one of the enabled operations.</returns>
    int NextOperation(IReadOnlyList<SchedulableOperation> enabled, int currentId, int step);

    /// <summary>Resolves a controlled boolean choice.</summary>
    bool NextBoolean(int step);

    /// <summary>Resolves a controlled integer choice in [0, <paramref name="maxValue"/>).</summary>
    int NextInteger(int maxValue, int step);

    /// <summary>Notifies the strategy that the current iteration finished.</summary>
    void CompleteIteration(IterationOutcome outcome);

    /// <summary>
    /// Returns a C# expression that recreates a strategy reproducing the current iteration as its first iteration,
    /// or <see langword="null"/> if the strategy cannot be reproduced from a seed (use the schedule trace instead).
    /// </summary>
    string? DescribeReproduction();

    /// <summary>Gets the seed driving the current iteration, if the strategy is randomized.</summary>
    ulong? CurrentIterationSeed { get; }
}
