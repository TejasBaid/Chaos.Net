using System.Collections.Concurrent;

namespace ChaosNet.Samples;

/// <summary>A message between election nodes.</summary>
public abstract record ElectionMessage(int From, int Term);

/// <summary>A candidate asks for a vote.</summary>
public sealed record RequestVote(int From, int Term) : ElectionMessage(From, Term)
{
    /// <inheritdoc />
    public override string ToString() => $"RequestVote(n{From}, t{Term})";
}

/// <summary>A vote response.</summary>
public sealed record VoteReply(int From, int Term, bool Granted) : ElectionMessage(From, Term)
{
    /// <inheritdoc />
    public override string ToString() => $"Vote(n{From}, t{Term}, {(Granted ? "yes" : "no")})";
}

/// <summary>A local election-timeout event, delivered through the node's own inbox.</summary>
public sealed record ElectionTimeout(int From) : ElectionMessage(From, -1)
{
    /// <inheritdoc />
    public override string ToString() => $"Timeout(n{From})";
}

/// <summary>
/// An asynchronous network that loses, duplicates and reorders messages. Every delivery is its own in-flight
/// operation, so deliveries reorder freely.
/// </summary>
/// <remarks>
/// Under test, the fate of every message (drop / duplicate / deliver) is a controlled choice, so the engine explores
/// and replays fault patterns systematically. In production (and in the naive stress baseline) faults happen at a
/// realistic rate, <see cref="ProductionFaultRate"/> each for drops and duplicates. That asymmetry is the point of
/// systematic fault injection: a fault pattern that production sees once in a million messages is an ordinary branch
/// for the scheduler.
/// </remarks>
public sealed class LossyNetwork
{
    /// <summary>Probability of a drop, and separately of a duplicate, per message in production.</summary>
    public const double ProductionFaultRate = 0.01;

    private readonly ControlledChannel<ElectionMessage>[] _inboxes;

    /// <summary>Creates a network of <paramref name="nodes"/> inboxes.</summary>
    public LossyNetwork(int nodes) =>
        _inboxes = [.. Enumerable.Range(0, nodes).Select(i => new ControlledChannel<ElectionMessage>(name: $"inbox[n{i}]"))];

    /// <summary>Gets the inbox of a node.</summary>
    public ControlledChannel<ElectionMessage> Inbox(int node) => _inboxes[node];

    /// <summary>Sends a message. Under test: a controlled choice of drop (1/4), duplicate (1/4) or deliver (1/2).</summary>
    public void Send(int to, ElectionMessage message)
    {
        int fate = Chaos.IsControlled ? Chaos.RandomInt(4) : ProductionFate();
        if (fate == 0)
        {
            Chaos.Log($"network drops {message} -> n{to}");
            return;
        }

        int copies = fate == 1 ? 2 : 1;
        if (copies == 2)
        {
            Chaos.Log($"network duplicates {message} -> n{to}");
        }

        for (int c = 0; c < copies; c++)
        {
            _ = ControlledTask.Run(() => _inboxes[to].SendAsync(message).AsTask(), $"{message}->n{to}");
        }
    }

    private static int ProductionFate()
    {
#pragma warning disable CHAOS006 // Production fault model: this branch never runs under a controlled test.
        double r = Random.Shared.NextDouble();
#pragma warning restore CHAOS006
        return r < ProductionFaultRate ? 0 : r < 2 * ProductionFaultRate ? 1 : 2;
    }
}

/// <summary>
/// Election safety specification: at most one leader per term. Raft's core safety property.
/// </summary>
public sealed class ElectionSafetyMonitor : ChaosMonitor
{
    private readonly ConcurrentDictionary<int, int> _leaders = new();

    /// <summary>Gets the first violation, if any (also checked in production mode).</summary>
    public string? Violation { get; private set; }

    /// <summary>Records that <paramref name="node"/> became leader of <paramref name="term"/>.</summary>
    public void OnLeaderElected(int term, int node)
    {
        int existing = _leaders.GetOrAdd(term, node);
        if (existing != node)
        {
            Violation ??= $"two leaders in term {term}: n{existing} and n{node}";
            Assert(false, Violation);
        }
    }
}

/// <summary>
/// Sample 3: Raft-style leader election over a lossy, duplicating, reordering network. Each node is an actor: one
/// loop handles all messages, so node state needs no locks. In the buggy version a candidate counts vote messages
/// instead of distinct voters, so a duplicated vote lets a candidate win with a minority. Another candidate can win
/// the same term with a real majority, and the cluster has two leaders.
/// </summary>
/// <remarks>The fix counts distinct voters (a set). It is the kind of bug that only appears when duplication and an interleaving coincide.</remarks>
public sealed class ElectionNode(int id, int clusterSize, LossyNetwork network, ElectionSafetyMonitor spec, bool buggy)
{
    private readonly HashSet<int> _voters = [];
    private int _term;
    private int? _votedFor;
    private int _votes;
    private Role _role = Role.Follower;

    private enum Role
    {
        Follower,
        Candidate,
        Leader,
    }

    /// <summary>Gets the node id.</summary>
    public int Id => id;

    /// <summary>Message loop.</summary>
    public async Task ServeAsync()
    {
        await foreach (ElectionMessage message in network.Inbox(id).ReadAllAsync())
        {
            Handle(message);
        }
    }

    /// <summary>Fires up to <paramref name="elections"/> randomized election timeouts.</summary>
    public async Task ElectionTimerAsync(int elections)
    {
        for (int i = 0; i < elections; i++)
        {
            await ControlledTask.Delay(TimeSpan.FromMilliseconds(1 + Chaos.RandomInt(3)));
            await network.Inbox(id).SendAsync(new ElectionTimeout(id));
        }
    }

    private void Handle(ElectionMessage message)
    {
        if (message is ElectionTimeout)
        {
            if (_role != Role.Leader)
            {
                StartElection();
            }

            return;
        }

        if (message.Term > _term)
        {
            _term = message.Term;
            _role = Role.Follower;
            _votedFor = null;
        }

        switch (message)
        {
            case RequestVote rv:
                bool grant = rv.Term == _term && (_votedFor is null || _votedFor == rv.From);
                if (grant)
                {
                    _votedFor = rv.From;
                }

                network.Send(rv.From, new VoteReply(id, _term, grant));
                break;

            case VoteReply { Granted: true } v when _role == Role.Candidate && v.Term == _term:
                int count;
                if (buggy)
                {
                    count = ++_votes; // BUG: counts messages, so a duplicated vote counts twice
                }
                else
                {
                    _voters.Add(v.From);
                    count = _voters.Count;
                }

                if (count > clusterSize / 2)
                {
                    _role = Role.Leader;
                    Chaos.Log($"n{id} becomes LEADER of term {_term} with {count} votes");
                    spec.OnLeaderElected(_term, id);
                }

                break;
        }
    }

    private void StartElection()
    {
        _term++;
        _role = Role.Candidate;
        _votedFor = id;
        _votes = 1;
        _voters.Clear();
        _voters.Add(id);
        Chaos.Log($"n{id} starts election for term {_term}");
        for (int peer = 0; peer < clusterSize; peer++)
        {
            if (peer != id)
            {
                network.Send(peer, new RequestVote(id, _term));
            }
        }
    }
}

/// <summary>Test harness for the leader election.</summary>
public static class LeaderElectionSample
{
    /// <summary>Five nodes, two election timeouts each, faulty network. At most one leader per term.</summary>
    public static async Task RunAsync(bool buggy)
    {
        const int nodes = 5;
        ElectionSafetyMonitor spec = Chaos.RegisterMonitor(new ElectionSafetyMonitor());
        var network = new LossyNetwork(nodes);
        ElectionNode[] cluster = [.. Enumerable.Range(0, nodes).Select(i => new ElectionNode(i, nodes, network, spec, buggy))];
        foreach (ElectionNode node in cluster)
        {
            _ = ControlledTask.Run(node.ServeAsync, $"n{node.Id}.serve");
        }

        await Task.WhenAll(cluster.Select(n => ControlledTask.Run(() => n.ElectionTimerAsync(elections: 2), $"n{n.Id}.timer")));
        await ControlledTask.Delay(TimeSpan.FromMilliseconds(20)); // let in-flight messages settle
        Chaos.Assert(spec.Violation is null, spec.Violation);
    }

    /// <summary>The buggy system (entry point for the CLI).</summary>
    public static Task BuggyTest() => RunAsync(buggy: true);

    /// <summary>The fixed system (entry point for the CLI).</summary>
    public static Task FixedTest() => RunAsync(buggy: false);
}
