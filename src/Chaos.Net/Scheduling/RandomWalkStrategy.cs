namespace ChaosNet.Scheduling;

/// <summary>
/// Random walk: at every scheduling point pick an enabled operation uniformly at random.
/// </summary>
/// <remarks>
/// Random walk is fair with probability 1 and is very good at shallow bugs that need a few context switches close
/// together. It is weak at bugs that need one operation to be starved for a long stretch: the probability of
/// not picking an operation for <c>k</c> consecutive steps decays like <c>(1 - 1/n)^k</c>.
/// </remarks>
public sealed class RandomWalkStrategy : ISchedulingStrategy
{
    private readonly ulong _baseSeed;
    private Xoshiro256 _rng;
    private ulong _iterationSeed;

    /// <summary>Creates a random-walk strategy.</summary>
    /// <param name="seed">Base seed. Iteration <c>i</c> uses a seed derived from it; iteration 0 uses it unchanged.</param>
    public RandomWalkStrategy(ulong seed)
    {
        _baseSeed = seed;
        _rng = new Xoshiro256(seed);
    }

    /// <inheritdoc />
    public string Name => "random";

    /// <inheritdoc />
    public bool IsFair => true;

    /// <summary>Gets the base seed.</summary>
    public ulong Seed => _baseSeed;

    /// <inheritdoc />
    public ulong? CurrentIterationSeed => _iterationSeed;

    /// <inheritdoc />
    public bool PrepareIteration(int iteration)
    {
        _iterationSeed = Xoshiro256.IterationSeed(_baseSeed, iteration);
        _rng = new Xoshiro256(_iterationSeed);
        return true;
    }

    /// <inheritdoc />
    public int NextOperation(IReadOnlyList<SchedulableOperation> enabled, int currentId, int step) =>
        enabled[_rng.Next(enabled.Count)].Id;

    /// <inheritdoc />
    public bool NextBoolean(int step) => _rng.NextBoolean();

    /// <inheritdoc />
    public int NextInteger(int maxValue, int step) => _rng.Next(maxValue);

    /// <inheritdoc />
    public void CompleteIteration(IterationOutcome outcome)
    {
    }

    /// <inheritdoc />
    public string? DescribeReproduction() => $"Strategies.Random(seed: 0x{_iterationSeed:x16})";

    /// <inheritdoc />
    public override string ToString() => $"random(seed=0x{_baseSeed:x16})";
}
