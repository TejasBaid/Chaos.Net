namespace ChaosNet.Scheduling;

/// <summary>
/// Round-robins between several strategies, one per iteration. Different strategies find different bugs, so a
/// portfolio is a robust default when you do not know the shape of the bug you are looking for.
/// </summary>
public sealed class PortfolioStrategy : ISchedulingStrategy
{
    private readonly ISchedulingStrategy[] _strategies;
    private readonly int[] _localIteration;
    private ISchedulingStrategy _current;

    /// <summary>Creates a portfolio of <paramref name="strategies"/>.</summary>
    public PortfolioStrategy(params ISchedulingStrategy[] strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        if (strategies.Length == 0)
        {
            throw new ArgumentException("A portfolio needs at least one strategy.", nameof(strategies));
        }

        _strategies = strategies;
        _localIteration = new int[strategies.Length];
        _current = strategies[0];
    }

    /// <inheritdoc />
    public string Name => "portfolio";

    /// <inheritdoc />
    public bool IsFair => _current.IsFair;

    /// <summary>Gets the strategy driving the current iteration.</summary>
    public ISchedulingStrategy Current => _current;

    /// <inheritdoc />
    public ulong? CurrentIterationSeed => _current.CurrentIterationSeed;

    /// <inheritdoc />
    public bool PrepareIteration(int iteration)
    {
        if (iteration == 0)
        {
            Array.Clear(_localIteration);
        }

        // Skip members that have exhausted their search space (e.g. DFS).
        for (int attempt = 0; attempt < _strategies.Length; attempt++)
        {
            int index = (iteration + attempt) % _strategies.Length;
            _current = _strategies[index];
            if (_current.PrepareIteration(_localIteration[index]++))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public int NextOperation(IReadOnlyList<SchedulableOperation> enabled, int currentId, int step) => _current.NextOperation(enabled, currentId, step);

    /// <inheritdoc />
    public bool NextBoolean(int step) => _current.NextBoolean(step);

    /// <inheritdoc />
    public int NextInteger(int maxValue, int step) => _current.NextInteger(maxValue, step);

    /// <inheritdoc />
    public void CompleteIteration(IterationOutcome outcome) => _current.CompleteIteration(outcome);

    /// <inheritdoc />
    public string? DescribeReproduction() => _current.DescribeReproduction();

    /// <inheritdoc />
    public override string ToString() => $"portfolio({string.Join(", ", _strategies.Select(s => s.ToString()))})";
}
