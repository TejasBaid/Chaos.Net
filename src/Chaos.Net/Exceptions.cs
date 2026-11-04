namespace ChaosNet;

/// <summary>Thrown by <see cref="Chaos.Assert(bool, string?, string?)"/> when a safety property is violated.</summary>
public sealed class ChaosAssertionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ChaosAssertionException(string message) : base(message)
    {
    }
}

/// <summary>
/// Thrown when code running under a controlled test uses a Chaos.Net API from a thread the scheduler does not
/// control (for example inside <see cref="Task.Run(Action)"/> or after <c>ConfigureAwait(false)</c>).
/// </summary>
public sealed class UncontrolledConcurrencyException : Exception
{
    /// <summary>Creates the exception.</summary>
    public UncontrolledConcurrencyException(string message) : base(message)
    {
    }
}

/// <summary>
/// Thrown when an execution does not follow a recorded schedule: the program offered different choices than the
/// trace (or DFS prefix) expects. This means the code under test is not deterministic under controlled scheduling.
/// </summary>
public sealed class NondeterminismException : Exception
{
    /// <summary>Creates the exception.</summary>
    public NondeterminismException(string message) : base(message)
    {
    }
}
