namespace ChaosNet.Scheduling;

/// <summary>
/// Probabilistic Concurrency Testing (Burckhardt, Kothari, Musuvathi, Nagarakatte, ASPLOS 2010).
/// </summary>
/// <remarks>
/// <para>
/// Every operation receives a distinct random priority when it is first seen, drawn from the values
/// <c>d, d+1, ...</c>. The scheduler always runs the highest-priority enabled operation. Before the iteration
/// starts, <c>d - 1</c> priority change points <c>k_1 .. k_{d-1}</c> are drawn uniformly from <c>[1, k]</c>. When
/// the run reaches step <c>k_i</c>, the priority of the operation about to run drops to <c>i</c>, below every
/// initial priority.
/// </para>
/// <para>
/// For a program with at most <c>n</c> operations and <c>k</c> steps, PCT finds any bug of depth <c>d</c> (a bug
/// whose manifestation needs <c>d</c> ordering constraints) in each iteration with probability at least
/// <c>1 / (n * k^(d-1))</c>. That bound does not depend on how many other interleavings there are. PCT
/// deliberately lets one operation run for long stretches, which is exactly what random walk is bad at.
/// </para>
/// <para>
/// Implementation notes. <c>k</c> is not known up front, so it is estimated as the largest number of
/// <em>choice points</em> (steps with at least two enabled operations) seen in earlier iterations. Only choice
/// points advance the change-point counter, so change points are never wasted on forced steps. New operations get
/// a random position among the non-demoted operations, as in the original algorithm's dynamic-thread extension.
/// </para>
/// </remarks>
public sealed class PctStrategy : ISchedulingStrategy
{
    private readonly ulong _baseSeed;
    private readonly int _initialStepsEstimate;
    private readonly List<int> _priorities = []; // highest first: non-demoted ops, then demoted ops by descending label
    private readonly Dictionary<int, int> _demotedLabel = [];
    private readonly HashSet<int> _known = [];
    private readonly Dictionary<int, int> _changePoints = []; // choice-point index -> label (1..d-1)
    private Xoshiro256 _rng;
    private ulong _iterationSeed;
    private int _choicePoints;
    private int _stepsEstimate;
    private int _iterationStepsEstimate;

    /// <summary>Creates a PCT strategy.</summary>
    /// <param name="depth">Bug depth <c>d</c>. PCT inserts <c>d - 1</c> priority change points per iteration.</param>
    /// <param name="seed">Base seed.</param>
    /// <param name="stepsEstimate">Initial estimate of <c>k</c> (choice points per iteration). 0 means learn it: the first iteration then runs with no change points.</param>
    public PctStrategy(int depth, ulong seed, int stepsEstimate = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(stepsEstimate);
        Depth = depth;
        _baseSeed = seed;
        _initialStepsEstimate = stepsEstimate;
        _stepsEstimate = stepsEstimate;
    }

    /// <summary>Gets the bug depth parameter <c>d</c>.</summary>
    public int Depth { get; }

    /// <inheritdoc />
    public string Name => "pct";

    /// <inheritdoc />
    public bool IsFair => false;

    /// <inheritdoc />
    public ulong? CurrentIterationSeed => _iterationSeed;

    /// <summary>Gets the change points (choice-point indices) used by the current iteration, ordered by step.</summary>
    public IReadOnlyList<int> CurrentChangePoints => [.. _changePoints.Keys.Order()];

    /// <inheritdoc />
    public bool PrepareIteration(int iteration)
    {
        if (iteration == 0)
        {
            _stepsEstimate = _initialStepsEstimate;
        }

        _iterationSeed = Xoshiro256.IterationSeed(_baseSeed, iteration);
        _rng = new Xoshiro256(_iterationSeed);
        _priorities.Clear();
        _demotedLabel.Clear();
        _known.Clear();
        _changePoints.Clear();
        _choicePoints = 0;
        _iterationStepsEstimate = _stepsEstimate;

        int k = _stepsEstimate;
        int count = Math.Min(Depth - 1, k);
        for (int label = 1; label <= count; label++)
        {
            int point;
            do
            {
                point = 1 + _rng.Next(k);
            }
            while (_changePoints.ContainsKey(point));
            _changePoints.Add(point, label);
        }

        return true;
    }

    /// <inheritdoc />
    public int NextOperation(IReadOnlyList<SchedulableOperation> enabled, int currentId, int step)
    {
        for (int i = 0; i < enabled.Count; i++)
        {
            int id = enabled[i].Id;
            if (_known.Add(id))
            {
                int nonDemoted = _priorities.Count - _demotedLabel.Count;
                _priorities.Insert(_rng.Next(nonDemoted + 1), id);
            }
        }

        if (enabled.Count < 2)
        {
            return enabled[0].Id;
        }

        _choicePoints++;
        int chosen = HighestEnabled(enabled);
        if (_changePoints.TryGetValue(_choicePoints, out int label))
        {
            Demote(chosen, label);
            chosen = HighestEnabled(enabled);
        }

        return chosen;
    }

    private int HighestEnabled(IReadOnlyList<SchedulableOperation> enabled)
    {
        foreach (int id in _priorities)
        {
            for (int i = 0; i < enabled.Count; i++)
            {
                if (enabled[i].Id == id)
                {
                    return id;
                }
            }
        }

        throw new InvalidOperationException("PCT invariant violated: no enabled operation has a priority.");
    }

    private void Demote(int id, int label)
    {
        _priorities.Remove(id);
        _demotedLabel.Remove(id);

        // Demoted operations live at the tail, ordered by descending label (a larger label is a higher priority).
        int index = _priorities.Count - _demotedLabel.Count;
        while (index < _priorities.Count && _demotedLabel[_priorities[index]] > label)
        {
            index++;
        }

        _priorities.Insert(index, id);
        _demotedLabel[id] = label;
    }

    /// <inheritdoc />
    public bool NextBoolean(int step) => _rng.NextBoolean();

    /// <inheritdoc />
    public int NextInteger(int maxValue, int step) => _rng.Next(maxValue);

    /// <inheritdoc />
    public void CompleteIteration(IterationOutcome outcome) => _stepsEstimate = Math.Max(_stepsEstimate, _choicePoints);

    /// <inheritdoc />
    public string? DescribeReproduction() =>
        $"Strategies.Pct(depth: {Depth}, seed: 0x{_iterationSeed:x16}, stepsEstimate: {_iterationStepsEstimate})";

    /// <inheritdoc />
    public override string ToString() => $"pct(depth={Depth}, seed=0x{_baseSeed:x16})";
}
