using System.Globalization;
using ChaosNet;
using ChaosNet.Cli;
using ChaosNet.Samples;
using ChaosNet.Scheduling;
using ChaosNet.Tracing;

return Cli.Run(args);

internal static class Cli
{
    private const string Usage = """
        chaos: Chaos.Net systematic concurrency testing CLI

        Usage:
          chaos replay <trace.chaos.json> [--assembly <test.dll>]   Re-execute a bug trace and print its timeline
          chaos show <trace.chaos.json>                             Print a trace's metadata, schedule and stored timeline
          chaos run --assembly <test.dll> --test <Ns.Type.Method> [options]
          chaos list --assembly <test.dll>                          List [ChaosFact] tests and static *Test() entry points
          chaos samples                                             List the built-in sample systems
          chaos sample <name> [--fixed] [options]                   Test a built-in sample system

        Options:
          --strategy random|pct|dfs|portfolio   (default: pct)
          --depth <d>                           PCT bug depth (default: 3)
          --preemptions <n>                     DFS preemption bound
          --iterations <n>                      (default: 10000)
          --seed <n|0xhex>                      base seed (default: fresh)
          --max-steps <n>                       (default: 10000)
          --out <dir>                           write bug traces here (default: ./chaos-traces)
          --quiet                               print only the summary line
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        try
        {
            var options = Options.Parse(args.Skip(1));
            return args[0] switch
            {
                "replay" => Replay(options),
                "show" => Show(options),
                "run" => RunTest(options),
                "list" => ListTests(options),
                "samples" => ListSamples(),
                "sample" => RunSample(options),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}"),
            };
        }
        catch (Exception e) when (e is ArgumentException or FileNotFoundException or FormatException or InvalidOperationException)
        {
            return Fail(e.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static int Replay(Options o)
    {
        string path = o.Positional.FirstOrDefault() ?? throw new ArgumentException("replay needs a trace file.");
        ScheduleTrace trace = ScheduleTrace.Load(path);
        Func<Task> body = ResolveBody(trace, o);
        Console.WriteLine($"Replaying {trace.TestName ?? "(unnamed test)"}: {trace.Steps} steps, {trace.Decisions.Count} decisions, recorded bug: {trace.BugKind?.ToString() ?? "none"}");
        TestReport report = TestEngine.Replay(body, trace);
        Console.WriteLine(report.Timeline);
        if (report.Reproduced == true)
        {
            Console.WriteLine($"REPRODUCED: {trace.BugKind} on the identical schedule.");
            return 0;
        }

        Console.WriteLine(report.FirstBug is { } bug
            ? $"DIVERGED: replay produced {bug.Kind}: {bug.Message}"
            : "DIVERGED: replay finished without the recorded bug.");
        return 1;
    }

    private static Func<Task> ResolveBody(ScheduleTrace trace, Options o)
    {
        string test = o.Get("--test") ?? trace.TestName ?? throw new ArgumentException("The trace has no test name; pass --test.");
        SampleInfo? sample = SampleCatalog.All.FirstOrDefault(s => test == $"sample:{s.Name}:buggy" || test == $"sample:{s.Name}:fixed");
        if (sample is not null)
        {
            bool buggy = test.EndsWith(":buggy", StringComparison.Ordinal);
            return () => sample.Run(buggy);
        }

        string assembly = o.Get("--assembly") ?? trace.AssemblyPath ?? throw new ArgumentException("The trace has no assembly path; pass --assembly.");
        return TestLoader.Load(assembly, test).Body;
    }

    private static int Show(Options o)
    {
        string path = o.Positional.FirstOrDefault() ?? throw new ArgumentException("show needs a trace file.");
        ScheduleTrace t = ScheduleTrace.Load(path);
        Console.WriteLine($"Test:        {t.TestName}");
        Console.WriteLine($"Assembly:    {t.AssemblyPath}");
        Console.WriteLine($"Strategy:    {t.Strategy}");
        Console.WriteLine($"Seed:        {t.SeedHex ?? "-"}");
        Console.WriteLine($"Reproduce:   {t.Reproduction ?? "(replay the trace)"}");
        Console.WriteLine($"Iteration:   {t.Iteration}");
        Console.WriteLine($"Bug:         {t.BugKind}: {t.BugMessage}");
        Console.WriteLine($"Steps:       {t.Steps} ({t.Decisions.Count} decisions)");
        Console.WriteLine($"Settings:    {t.Settings}");
        Console.WriteLine($"Schedule:    {t.Schedule}");
        if (t.Timeline is not null)
        {
            Console.WriteLine();
            Console.WriteLine(t.Timeline);
        }

        return 0;
    }

    private static int RunTest(Options o)
    {
        string assembly = o.Get("--assembly") ?? throw new ArgumentException("run needs --assembly.");
        string test = o.Get("--test") ?? throw new ArgumentException("run needs --test.");
        (Func<Task> body, string name, string path) = TestLoader.Load(assembly, test);
        return Explore(body, o, name, path);
    }

    private static int ListTests(Options o)
    {
        string assembly = o.Get("--assembly") ?? throw new ArgumentException("list needs --assembly.");
        foreach (string t in TestLoader.List(assembly))
        {
            Console.WriteLine(t);
        }

        return 0;
    }

    private static int ListSamples()
    {
        foreach (SampleInfo s in SampleCatalog.All)
        {
            Console.WriteLine($"{s.Name,-20} {s.ExpectedBug,-17} {s.Description}");
        }

        return 0;
    }

    private static int RunSample(Options o)
    {
        string name = o.Positional.FirstOrDefault() ?? throw new ArgumentException("sample needs a name (see 'chaos samples').");
        SampleInfo sample = SampleCatalog.Get(name);
        bool buggy = !o.Has("--fixed");
        return Explore(() => sample.Run(buggy), o, $"sample:{sample.Name}:{(buggy ? "buggy" : "fixed")}", null);
    }

    private static int Explore(Func<Task> body, Options o, string testName, string? assemblyPath)
    {
        ulong? seed = o.Get("--seed") is { } s ? ParseSeed(s) : null;
        int depth = o.GetInt("--depth") ?? 3;
        ISchedulingStrategy strategy = (o.Get("--strategy") ?? "pct") switch
        {
            "random" => Strategies.Random(seed),
            "pct" => Strategies.Pct(depth, seed),
            "dfs" => Strategies.Dfs(preemptionBound: o.GetInt("--preemptions")),
            "portfolio" => Strategies.Portfolio(Strategies.Random(seed), Strategies.Pct(depth, seed is { } x ? x + 1 : null)),
            var other => throw new ArgumentException($"Unknown strategy '{other}'."),
        };

        var config = new TestConfig
        {
            Iterations = o.GetInt("--iterations") ?? 10_000,
            Strategy = strategy,
            MaxSteps = o.GetInt("--max-steps") ?? 10_000,
            TestName = testName,
            TestAssemblyPath = assemblyPath,
            TraceDirectory = o.Get("--out") ?? Path.Combine(Environment.CurrentDirectory, "chaos-traces"),
        };

        TestReport report = TestEngine.Run(body, config);
        Console.WriteLine(o.Has("--quiet") ? report.Summary() : report.ToString());
        return report.HasBugs ? 1 : 0;
    }

    private static ulong ParseSeed(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ulong.Parse(s, CultureInfo.InvariantCulture);
}

internal sealed class Options
{
    private readonly Dictionary<string, string?> _named = new(StringComparer.Ordinal);

    public List<string> Positional { get; } = [];

    public static Options Parse(IEnumerable<string> args)
    {
        var o = new Options();
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            string a = list[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                bool flag = a is "--fixed" or "--quiet";
                if (flag || i + 1 >= list.Count)
                {
                    o._named[a] = null;
                }
                else
                {
                    o._named[a] = list[++i];
                }
            }
            else
            {
                o.Positional.Add(a);
            }
        }

        return o;
    }

    public bool Has(string name) => _named.ContainsKey(name);

    public string? Get(string name) => _named.TryGetValue(name, out string? v) ? v : null;

    public int? GetInt(string name) => Get(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : null;
}
