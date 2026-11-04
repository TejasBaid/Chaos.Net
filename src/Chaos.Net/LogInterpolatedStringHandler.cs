using System.Runtime.CompilerServices;
using ChaosNet.Runtime;

namespace ChaosNet;

/// <summary>
/// Interpolated-string handler for <see cref="Chaos.Log(ref LogInterpolatedStringHandler)"/>. Formatting only
/// happens while a bug timeline is being recorded, so logging in hot paths costs a branch during exploration.
/// </summary>
[InterpolatedStringHandler]
public ref struct LogInterpolatedStringHandler
{
    private DefaultInterpolatedStringHandler _inner;

    /// <summary>Creates the handler (called by the compiler).</summary>
    public LogInterpolatedStringHandler(int literalLength, int formattedCount, out bool shouldAppend)
    {
        Enabled = ChaosRuntime.Current is { IsRecording: true };
        shouldAppend = Enabled;
        _inner = Enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    internal readonly bool Enabled { get; }

    /// <summary>Appends a literal (called by the compiler).</summary>
    public void AppendLiteral(string value) => _inner.AppendLiteral(value);

    /// <summary>Appends a value (called by the compiler).</summary>
    public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);

    /// <summary>Appends a formatted value (called by the compiler).</summary>
    public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);

    /// <summary>Appends a string (called by the compiler).</summary>
    public void AppendFormatted(string? value) => _inner.AppendFormatted(value);

    internal string ToStringAndClear() => _inner.ToStringAndClear();
}
