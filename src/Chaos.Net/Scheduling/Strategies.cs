using ChaosNet.Scheduling;
using ChaosNet.Tracing;

namespace ChaosNet;

/// <summary>Factory methods for the built-in exploration strategies.</summary>
public static class Strategies
{
    /// <summary>Random walk. See <see cref="RandomWalkStrategy"/>.</summary>
    /// <param name="seed">Base seed; a fresh one is generated (and reported) when omitted.</param>
    public static RandomWalkStrategy Random(ulong? seed = null) => new(seed ?? Xoshiro256.NewSeed());

    /// <summary>Probabilistic Concurrency Testing. See <see cref="PctStrategy"/>.</summary>
    /// <param name="depth">Bug depth <c>d</c> (number of ordering constraints the bug needs). <c>d - 1</c> change points are used.</param>
    /// <param name="seed">Base seed; a fresh one is generated (and reported) when omitted.</param>
    /// <param name="stepsEstimate">Initial estimate of the number of choice points per execution; 0 learns it.</param>
    public static PctStrategy Pct(int depth = 3, ulong? seed = null, int stepsEstimate = 0) =>
        new(depth, seed ?? Xoshiro256.NewSeed(), stepsEstimate);

    /// <summary>Bounded exhaustive depth-first search. See <see cref="DfsStrategy"/>.</summary>
    /// <param name="maxDepth">Maximum number of choice points per execution.</param>
    /// <param name="preemptionBound">Optional context bound (maximum preemptions per execution).</param>
    public static DfsStrategy Dfs(int maxDepth = 10_000, int? preemptionBound = null) => new(maxDepth, preemptionBound);

    /// <summary>Replays a recorded schedule. See <see cref="ReplayStrategy"/>.</summary>
    public static ReplayStrategy Replay(ScheduleTrace trace) => new(trace);

    /// <summary>Round-robins between strategies per iteration. See <see cref="PortfolioStrategy"/>.</summary>
    public static PortfolioStrategy Portfolio(params ISchedulingStrategy[] strategies) => new(strategies);
}
