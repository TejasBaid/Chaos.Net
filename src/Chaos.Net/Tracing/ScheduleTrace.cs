using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChaosNet.Tracing;

/// <summary>Execution settings that change program behaviour and therefore must match during replay.</summary>
public sealed record TraceSettings
{
    /// <summary>See <see cref="TestConfig.MaxSteps"/>.</summary>
    public int MaxSteps { get; init; }

    /// <summary>See <see cref="TestConfig.ReportLivelocks"/>.</summary>
    public bool ReportLivelocks { get; init; }

    /// <summary>See <see cref="TestConfig.TimerPolicy"/>.</summary>
    public TimerPolicy TimerPolicy { get; init; }

    /// <summary>See <see cref="TestConfig.UncontrolledConcurrency"/>.</summary>
    public UncontrolledConcurrencyPolicy UncontrolledConcurrency { get; init; }

    /// <summary>See <see cref="TestConfig.FailOnUnhandledExceptionsInOperations"/>.</summary>
    public bool FailOnUnhandledExceptionsInOperations { get; init; }

    /// <summary>See <see cref="TestConfig.LivenessTemperatureThreshold"/>.</summary>
    public int LivenessTemperatureThreshold { get; init; }

    /// <summary>See <see cref="TestConfig.ReportBlockedOperationsAsDeadlock"/>.</summary>
    public bool ReportBlockedOperationsAsDeadlock { get; init; }
}

/// <summary>
/// A replayable record of one execution: the decision sequence plus the settings and metadata needed to reproduce
/// it. Traces serialize to a small JSON file. The schedule itself uses the compact <see cref="ScheduleCodec"/> encoding.
/// </summary>
public sealed class ScheduleTrace
{
    /// <summary>The current trace format identifier.</summary>
    public const string CurrentFormat = "chaos-trace/1";

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private IReadOnlyList<Decision>? _decisions;
    private string _schedule = string.Empty;

    /// <summary>Gets the trace format identifier.</summary>
    public string Format { get; init; } = CurrentFormat;

    /// <summary>Gets the fully-qualified test name (<c>Namespace.Type.Method</c>) if known.</summary>
    public string? TestName { get; init; }

    /// <summary>Gets the simple name of the assembly that contains the test, if known.</summary>
    public string? AssemblyName { get; init; }

    /// <summary>Gets the path of the assembly that contains the test when the trace was written, if known.</summary>
    public string? AssemblyPath { get; init; }

    /// <summary>Gets a description of the strategy that produced the execution.</summary>
    public string Strategy { get; init; } = "unknown";

    /// <summary>Gets a C# expression that recreates a strategy reproducing this execution from a seed, if available.</summary>
    public string? Reproduction { get; init; }

    /// <summary>Gets the iteration seed in hex (<c>0x...</c>), if the strategy was randomized.</summary>
    public string? SeedHex { get; init; }

    /// <summary>Gets the iteration index that produced the trace.</summary>
    public int Iteration { get; init; }

    /// <summary>Gets the behaviour-affecting settings of the execution.</summary>
    public TraceSettings Settings { get; init; } = new();

    /// <summary>Gets the bug that the execution exhibited, if any.</summary>
    public BugKind? BugKind { get; init; }

    /// <summary>Gets the bug message, if any.</summary>
    public string? BugMessage { get; init; }

    /// <summary>Gets the number of scheduling steps in the execution.</summary>
    public int Steps { get; init; }

    /// <summary>Gets the operations of the execution as <c>id: label</c> strings.</summary>
    public IReadOnlyList<string>? Operations { get; init; }

    /// <summary>Gets when the trace was created.</summary>
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Gets the encoded schedule (see <see cref="ScheduleCodec"/>).</summary>
    public string Schedule
    {
        get => _schedule;
        init
        {
            _schedule = value ?? string.Empty;
            _decisions = null;
        }
    }

    /// <summary>Gets the rendered timeline of the execution, if it was recorded.</summary>
    public string? Timeline { get; init; }

    /// <summary>Gets the decoded decision sequence.</summary>
    [JsonIgnore]
    public IReadOnlyList<Decision> Decisions => _decisions ??= ScheduleCodec.Decode(_schedule);

    /// <summary>Gets the iteration seed, if any.</summary>
    [JsonIgnore]
    public ulong? Seed => SeedHex is { Length: > 2 } s
        ? ulong.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : null;

    /// <summary>Creates a trace from decisions.</summary>
    public static ScheduleTrace FromDecisions(IReadOnlyList<Decision> decisions, TraceSettings settings, string strategy = "manual") =>
        new() { Schedule = ScheduleCodec.Encode(decisions), Settings = settings, Strategy = strategy, Steps = decisions.Count(d => d.Kind == DecisionKind.Schedule) };

    /// <summary>Formats a seed the way traces store it.</summary>
    public static string FormatSeed(ulong seed) => $"0x{seed:x16}";

    /// <summary>Serializes the trace to JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, s_json);

    /// <summary>Parses a trace from JSON.</summary>
    /// <exception cref="FormatException">The JSON is not a Chaos.Net trace.</exception>
    public static ScheduleTrace FromJson(string json)
    {
        ScheduleTrace trace = JsonSerializer.Deserialize<ScheduleTrace>(json, s_json)
            ?? throw new FormatException("Empty trace document.");
        if (!string.Equals(trace.Format, CurrentFormat, StringComparison.Ordinal))
        {
            throw new FormatException($"Unsupported trace format '{trace.Format}' (expected '{CurrentFormat}').");
        }

        return trace;
    }

    /// <summary>Writes the trace to <paramref name="path"/>, creating the directory if necessary.</summary>
    public void Save(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, ToJson());
    }

    /// <summary>Reads a trace from <paramref name="path"/>.</summary>
    public static ScheduleTrace Load(string path) => FromJson(File.ReadAllText(path));
}
