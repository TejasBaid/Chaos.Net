using ChaosNet.Runtime;

namespace ChaosNet;

/// <summary>
/// A shared memory cell whose asynchronous accessors are scheduling points. Model shared state (a balance, a
/// version, a flag) with it to let the scheduler interleave other operations between every read and write. In
/// production mode reads and writes are plain, lock-protected accesses that complete synchronously.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
/// <remarks>Reads and writes are recorded in the bug timeline, so a lost update shows up as two reads of the same value followed by two writes.</remarks>
public sealed class SharedRegister<T>
{
    private readonly Lock _gate = new();
    private T _value;

    /// <summary>Creates a register.</summary>
    /// <param name="initialValue">The initial value.</param>
    /// <param name="name">Name used in reports.</param>
    public SharedRegister(T initialValue, string? name = null)
    {
        _value = initialValue;
        Name = name ?? "register";
    }

    /// <summary>Gets the register name.</summary>
    public string Name { get; }

    /// <summary>Gets or sets the value directly, without a scheduling point. Use for setup and final checks.</summary>
    public T Value
    {
        get
        {
            lock (_gate)
            {
                return _value;
            }
        }

        set
        {
            lock (_gate)
            {
                _value = value;
            }
        }
    }

    /// <summary>Reads the value after a scheduling point.</summary>
    public async ValueTask<T> ReadAsync()
    {
        if (ChaosRuntime.ForApi() is { } rt)
        {
            await new InterleaveAwaitable(rt, yieldInProduction: false);
            if (rt.IsRecording)
            {
                rt.Note($"read {Name} = {_value}");
            }

            return _value;
        }

        return Value;
    }

    /// <summary>Writes the value after a scheduling point.</summary>
    public async ValueTask WriteAsync(T value)
    {
        if (ChaosRuntime.ForApi() is { } rt)
        {
            await new InterleaveAwaitable(rt, yieldInProduction: false);
            if (rt.IsRecording)
            {
                rt.Note($"write {Name} := {value}");
            }

            _value = value;
            return;
        }

        Value = value;
    }

    /// <summary>Atomically replaces the value with <paramref name="value"/> if it equals <paramref name="expected"/> (after one scheduling point).</summary>
    /// <returns><see langword="true"/> if the value was replaced.</returns>
    public async ValueTask<bool> CompareExchangeAsync(T expected, T value)
    {
        if (ChaosRuntime.ForApi() is { } rt)
        {
            await new InterleaveAwaitable(rt, yieldInProduction: false);
            bool ok = EqualityComparer<T>.Default.Equals(_value, expected);
            if (rt.IsRecording)
            {
                rt.Note(ok ? $"cas {Name}: {expected} -> {value}" : $"cas {Name} failed (is {_value}, expected {expected})");
            }

            if (ok)
            {
                _value = value;
            }

            return ok;
        }

        lock (_gate)
        {
            if (!EqualityComparer<T>.Default.Equals(_value, expected))
            {
                return false;
            }

            _value = value;
            return true;
        }
    }

    /// <summary>Atomically applies <paramref name="update"/> (after one scheduling point) and returns the new value.</summary>
    public async ValueTask<T> UpdateAsync(Func<T, T> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (ChaosRuntime.ForApi() is { } rt)
        {
            await new InterleaveAwaitable(rt, yieldInProduction: false);
            T old = _value;
            _value = update(old);
            if (rt.IsRecording)
            {
                rt.Note($"update {Name}: {old} -> {_value}");
            }

            return _value;
        }

        lock (_gate)
        {
            _value = update(_value);
            return _value;
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name} = {Value}";
}
