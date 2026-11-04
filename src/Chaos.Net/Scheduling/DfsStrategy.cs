namespace ChaosNet.Scheduling;

/// <summary>
/// Stateless, bounded depth-first search over the tree of scheduling and nondeterministic choices.
/// </summary>
/// <remarks>
/// <para>
/// Each iteration replays the current prefix of choices and extends it with the first alternative at every new
/// choice point. After the iteration, the deepest choice point that still has an untried alternative is advanced,
/// and everything below it is discarded (classic stateless model checking à la VeriSoft/CHESS). The search ends
/// once every alternative of every choice point has been explored. At that point
/// <see cref="IsExhausted"/> is <see langword="true"/> and every distinct execution (up to the bounds) has run exactly once.
/// </para>
/// <para>
/// Steps with a single enabled operation are not choice points and do not deepen the tree. <c>maxDepth</c> bounds
/// the number of choice points explored per execution. Beyond it the strategy keeps running the current operation
/// (no preemption) and <see cref="DepthBoundHit"/> is set. The optional preemption bound implements iterative context
/// bounding (Musuvathi and Qadeer, PLDI 2007): an execution may preempt a still-enabled operation at most that many
/// times. Most concurrency bugs need very few preemptions, so small bounds give strong coverage cheaply.
/// </para>
/// </remarks>
public sealed class DfsStrategy : ISchedulingStrategy
{
    private readonly List<Frame> _stack = [];
    private readonly List<int> _candidates = [];
    private int _position;
    private int _preemptions;

    /// <summary>Creates a DFS strategy.</summary>
    /// <param name="maxDepth">Maximum number of choice points explored per execution.</param>
    /// <param name="preemptionBound">Optional maximum number of preemptions per execution.</param>
    public DfsStrategy(int maxDepth = 10_000, int? preemptionBound = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);
        if (preemptionBound is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(preemptionBound));
        }

        MaxDepth = maxDepth;
        PreemptionBound = preemptionBound;
    }

    /// <summary>Gets the maximum number of choice points per execution.</summary>
    public int MaxDepth { get; }

    /// <summary>Gets the preemption bound, if any.</summary>
    public int? PreemptionBound { get; }

    /// <summary>Gets whether every execution within the bounds has been explored.</summary>
    public bool IsExhausted { get; private set; }

    /// <summary>Gets whether any execution was cut off by <see cref="MaxDepth"/>. If so, exhaustion is relative to that bound.</summary>
    public bool DepthBoundHit { get; private set; }

    /// <inheritdoc />
    public string Name => PreemptionBound is { } pb ? $"dfs(pb={pb})" : "dfs";

    /// <inheritdoc />
    public bool IsFair => false;

    /// <inheritdoc />
    public ulong? CurrentIterationSeed => null;

    /// <inheritdoc />
    public bool PrepareIteration(int iteration)
    {
        if (iteration == 0)
        {
            _stack.Clear();
            IsExhausted = false;
            DepthBoundHit = false;
        }

        _position = 0;
        _preemptions = 0;
        return !IsExhausted;
    }

    /// <inheritdoc />
    public int NextOperation(IReadOnlyList<SchedulableOperation> enabled, int currentId, int step)
    {
        // Order candidates so that "continue the current operation" is always alternative 0 (non-preemptive first).
        _candidates.Clear();
        bool currentEnabled = false;
        for (int i = 0; i < enabled.Count; i++)
        {
            if (enabled[i].Id == currentId)
            {
                currentEnabled = true;
                break;
            }
        }

        if (currentEnabled)
        {
            _candidates.Add(currentId);
            if (PreemptionBound is not { } bound || _preemptions < bound)
            {
                for (int i = 0; i < enabled.Count; i++)
                {
                    if (enabled[i].Id != currentId)
                    {
                        _candidates.Add(enabled[i].Id);
                    }
                }
            }
        }
        else
        {
            for (int i = 0; i < enabled.Count; i++)
            {
                _candidates.Add(enabled[i].Id);
            }
        }

        int chosen = _candidates[Choose(_candidates.Count)];
        if (currentEnabled && chosen != currentId)
        {
            _preemptions++;
        }

        return chosen;
    }

    /// <inheritdoc />
    public bool NextBoolean(int step) => Choose(2) == 1;

    /// <inheritdoc />
    public int NextInteger(int maxValue, int step) => Choose(Math.Max(1, maxValue));

    private int Choose(int count)
    {
        if (count <= 1)
        {
            return 0;
        }

        if (_position >= MaxDepth)
        {
            DepthBoundHit = true;
            return 0;
        }

        if (_position < _stack.Count)
        {
            Frame frame = _stack[_position];
            if (frame.Count != count)
            {
                throw new NondeterminismException(
                    $"DFS replay diverged at choice point {_position}: expected {frame.Count} alternatives but found {count}. " +
                    "The code under test is not deterministic under controlled scheduling (uncontrolled concurrency, wall-clock time, or unseeded randomness).");
            }

            _position++;
            return frame.Index;
        }

        _stack.Add(new Frame(count, 0));
        _position++;
        return 0;
    }

    /// <inheritdoc />
    public void CompleteIteration(IterationOutcome outcome)
    {
        if (_stack.Count > _position)
        {
            _stack.RemoveRange(_position, _stack.Count - _position);
        }

        while (_stack.Count > 0)
        {
            Frame top = _stack[^1];
            if (top.Index + 1 < top.Count)
            {
                _stack[^1] = top with { Index = top.Index + 1 };
                return;
            }

            _stack.RemoveAt(_stack.Count - 1);
        }

        IsExhausted = true;
    }

    /// <inheritdoc />
    public string? DescribeReproduction() => null;

    /// <inheritdoc />
    public override string ToString() =>
        PreemptionBound is { } pb ? $"dfs(maxDepth={MaxDepth}, preemptionBound={pb})" : $"dfs(maxDepth={MaxDepth})";

    private readonly record struct Frame(int Count, int Index);
}
