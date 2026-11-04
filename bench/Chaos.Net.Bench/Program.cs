using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ChaosNet;
using ChaosNet.Samples;
using ChaosNet.Scheduling;

// Evaluation harness: iterations-to-first-bug and time-to-first-bug for each strategy on each buggy sample,
// averaged over many seeds, against a naive stress-testing baseline. Run in Release:
//   dotnet run -c Release --project bench/Chaos.Net.Bench -- [--seeds 50] [--max-iterations 20000] [--naive-trials 5] [--naive-budget-ms 5000] [--out bench/results/results.md]

var options = BenchOptions.Parse(args);
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var strategies = new (string Name, Func<ulong, SampleInfo, ISchedulingStrategy> Create, bool Seeded)[]
{
    ("Random walk", (seed, _) => Strategies.Random(seed), true),
    ("PCT d=2", (seed, _) => Strategies.Pct(2, seed), true),
    ("PCT d=3", (seed, _) => Strategies.Pct(3, seed), true),
    ("DFS", (_, _) => Strategies.Dfs(), false),
    ("DFS pb=2", (_, _) => Strategies.Dfs(preemptionBound: 2), false),
};

// Warm up the JIT so the first measured run is not penalized.
foreach (SampleInfo s in SampleCatalog.All)
{
    TestEngine.Run(() => s.Fixed(), new TestConfig { Iterations = 200, Strategy = Strategies.Random(1), VerifyBugsByReplay = false });
}

var rows = new List<Row>();
foreach (SampleInfo sample in SampleCatalog.All.Where(s => options.Only is null || s.Name == options.Only))
{
    var row = new Row(sample);
    foreach (var (name, create, seeded) in strategies)
    {
        var cell = new Cell(name);
        int trials = seeded ? options.Seeds : 1;
        for (int t = 0; t < trials; t++)
        {
            ulong seed = (ulong)(t + 1);
            TestReport report = TestEngine.Run(() => sample.Buggy(), new TestConfig
            {
                Iterations = options.MaxIterations,
                Strategy = create(seed, sample),
                MaxSteps = sample.MaxSteps,
                VerifyBugsByReplay = false,
            });
            cell.Add(report.FirstBug is { } bug ? bug.Iteration + 1 : null, report.Elapsed, report.Iterations, report.SearchExhausted);
            if (report.FirstBug is { } b && b.Kind != sample.ExpectedBug)
            {
                Console.Error.WriteLine($"warning: {sample.Name}/{name} found {b.Kind} (expected {sample.ExpectedBug}): {b.Message}");
            }
        }

        Console.WriteLine($"{sample.Name,-20} {name,-12} {cell.Describe()}");
        row.Cells.Add(cell);
    }

    var naive = new Cell("Naive stress");
    for (int t = 0; t < options.NaiveTrials; t++)
    {
        NaiveStressResult r = NaiveStress.Run(() => sample.Buggy(), options.NaiveMaxRuns, TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(options.NaiveBudgetMs), stopOnFirstFailure: true);
        naive.Add(r.FirstFailureRun, r.Elapsed, r.Runs, exhausted: false);
    }

    Console.WriteLine($"{sample.Name,-20} {"Naive stress",-12} {naive.Describe()}");
    row.Cells.Add(naive);
    rows.Add(row);
}

string markdown = Render(rows, options);
if (options.Only is null && options.Sweep)
{
    markdown += Environment.NewLine + Sweep(options);
}

if (options.Only is null)
{
    markdown += Environment.NewLine + Throughput();
}

Console.WriteLine();
Console.WriteLine(markdown);
if (options.Out is { } outPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    File.WriteAllText(outPath, markdown);
    Console.WriteLine($"Wrote {Path.GetFullPath(outPath)}");
}

// Raw exploration speed on the fixed samples (no bugs, so every iteration runs to completion).
static string Throughput()
{
    var sb = new StringBuilder();
    sb.AppendLine("**Throughput** (fixed versions, random walk, 5,000 iterations each, single scheduler thread):");
    sb.AppendLine();
    sb.AppendLine("| Sample | Steps / iteration | Iterations / s | Scheduling steps / s |");
    sb.AppendLine("|---|---|---|---|");
    foreach (SampleInfo sample in SampleCatalog.All)
    {
        TestReport r = TestEngine.Run(() => sample.Fixed(), new TestConfig { Iterations = 5000, Strategy = Strategies.Random(1), MaxSteps = sample.MaxSteps });
        double secs = r.Elapsed.TotalSeconds;
        string line = $"| `{sample.Name}` | {(double)r.TotalSteps / r.Iterations:N1} | {r.Iterations / secs:N0} | {r.TotalSteps / secs:N0} |";
        Console.WriteLine(line);
        sb.AppendLine(line);
    }

    return sb.ToString();
}

// Scaling study on the depth-3 checkpoint bug: the scan that must run uninterrupted is 2N steps long.
static string Sweep(BenchOptions o)
{
    var sb = new StringBuilder();
    sb.AppendLine($"**Scaling study, `checkpoint-depth3`**: N cells per transaction (the checkpoint scan is 2N steps). " +
                  $"Mean iterations to first bug over {o.SweepSeeds} seeds (cap {o.MaxIterations:N0}); naive stress: {o.NaiveTrials} trials of up to {o.SweepNaiveBudgetMs / 1000.0:0.#} s.");
    sb.AppendLine();
    sb.AppendLine("| N | Random walk | PCT d=3 | DFS pb=2 | Naive stress |");
    sb.AppendLine("|---|---|---|---|---|");
    foreach (int n in new[] { 2, 3, 4, 6, 8, 12 })
    {
        Func<Task> body = () => CheckpointSample.RunAsync(buggy: true, cellsPerTransaction: n);
        Cell Seeded(string name, Func<ulong, ISchedulingStrategy> make, int seeds)
        {
            var cell = new Cell(name);
            for (int t = 0; t < seeds; t++)
            {
                TestReport r = TestEngine.Run(body, new TestConfig { Iterations = o.MaxIterations, Strategy = make((ulong)(t + 1)), VerifyBugsByReplay = false });
                cell.Add(r.FirstBug is { } b ? b.Iteration + 1 : null, r.Elapsed, r.Iterations, r.SearchExhausted);
            }

            return cell;
        }

        Cell random = Seeded("random", s => Strategies.Random(s), o.SweepSeeds);
        Cell pct = Seeded("pct3", s => Strategies.Pct(3, s), o.SweepSeeds);
        Cell dfs = Seeded("dfs-pb2", _ => Strategies.Dfs(preemptionBound: 2), 1);
        var naive = new Cell("naive");
        for (int t = 0; t < o.NaiveTrials; t++)
        {
            NaiveStressResult r = NaiveStress.Run(body, o.NaiveMaxRuns, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(o.SweepNaiveBudgetMs), stopOnFirstFailure: true);
            naive.Add(r.FirstFailureRun, r.Elapsed, r.Runs, exhausted: false);
        }

        string line = $"| {n} | {random.DescribeIterations()} | {pct.DescribeIterations()} | {dfs.DescribeIterations()} | {naive.DescribeIterations()} |";
        Console.WriteLine(line);
        sb.AppendLine(line);
    }

    return sb.ToString();
}

static string Render(List<Row> rows, BenchOptions o)
{
    var sb = new StringBuilder();
    string cpu = Sysctl("machdep.cpu.brand_string") ?? RuntimeInformation.ProcessArchitecture.ToString();
    sb.AppendLine($"Machine: {cpu}, {Environment.ProcessorCount} logical cores, {RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}, Release build.");
    sb.AppendLine($"Randomized strategies: {o.Seeds} seeds each, at most {o.MaxIterations:N0} iterations per seed. DFS is deterministic (one run, same cap). " +
                  $"Naive stress: {o.NaiveTrials} trials, each up to {o.NaiveMaxRuns:N0} real runs on the thread pool or {o.NaiveBudgetMs / 1000.0:0.#} s.");
    sb.AppendLine();
    sb.AppendLine("**Iterations (or runs) to first bug**: success rate, then mean / median over the successful trials.");
    sb.AppendLine();
    AppendTable(sb, rows, c => c.DescribeIterations());
    sb.AppendLine();
    sb.AppendLine("**Wall-clock time to first bug**: mean over successful trials (ms). For failed trials, the time spent before giving up is shown in parentheses.");
    sb.AppendLine();
    AppendTable(sb, rows, c => c.DescribeTime());
    return sb.ToString();
}

static void AppendTable(StringBuilder sb, List<Row> rows, Func<Cell, string> describe)
{
    List<string> headers = rows[0].Cells.Select(c => c.Name).ToList();
    sb.AppendLine("| Sample (bug) | " + string.Join(" | ", headers) + " |");
    sb.AppendLine("|---|" + string.Concat(headers.Select(_ => "---|")));
    foreach (Row r in rows)
    {
        sb.AppendLine($"| `{r.Sample.Name}` ({r.Sample.ExpectedBug}) | " + string.Join(" | ", r.Cells.Select(describe)) + " |");
    }
}

static string? Sysctl(string key)
{
    if (!OperatingSystem.IsMacOS())
    {
        return null;
    }

    try
    {
        var psi = new ProcessStartInfo("sysctl", ["-n", key]) { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        string s = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return s.Length > 0 ? s : null;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return null;
    }
}

internal sealed class Row(SampleInfo sample)
{
    public SampleInfo Sample { get; } = sample;

    public List<Cell> Cells { get; } = [];
}

internal sealed class Cell(string name)
{
    private readonly List<int> _iterations = [];
    private readonly List<double> _times = [];
    private readonly List<double> _failedTimes = [];
    private int _trials;
    private int _maxExplored;
    private bool _exhausted;

    public string Name { get; } = name;

    public void Add(int? iterationsToBug, TimeSpan elapsed, int explored, bool exhausted)
    {
        _trials++;
        _maxExplored = Math.Max(_maxExplored, explored);
        _exhausted |= exhausted;
        if (iterationsToBug is { } i)
        {
            _iterations.Add(i);
            _times.Add(elapsed.TotalMilliseconds);
        }
        else
        {
            _failedTimes.Add(elapsed.TotalMilliseconds);
        }
    }

    private string Rate => $"{_iterations.Count}/{_trials}";

    public string DescribeIterations()
    {
        if (_iterations.Count == 0)
        {
            return $"0/{_trials} (none in {_maxExplored:N0}{(_exhausted ? ", exhausted" : string.Empty)})";
        }

        double mean = _iterations.Average();
        int median = _iterations.Order().ElementAt(_iterations.Count / 2);
        return _trials == 1 ? $"1/1 · {_iterations[0]:N0}" : $"{Rate} · {mean:N1} / {median:N0}";
    }

    public string DescribeTime()
    {
        string failed = _failedTimes.Count > 0 ? $" ({_failedTimes.Average():N0} ms ×{_failedTimes.Count} failed)" : string.Empty;
        return _times.Count == 0 ? $"–{failed}" : $"{_times.Average():N1}{failed}";
    }

    public string Describe() => $"{DescribeIterations(),-40} time {DescribeTime()}";
}

internal sealed record BenchOptions(int Seeds, int MaxIterations, int NaiveTrials, int NaiveMaxRuns, int NaiveBudgetMs, string? Out, string? Only, bool Sweep, int SweepSeeds, int SweepNaiveBudgetMs)
{
    public static BenchOptions Parse(string[] args)
    {
        string? Get(string n) => Array.IndexOf(args, n) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        int GetInt(string n, int d) => Get(n) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : d;
        return new BenchOptions(
            Seeds: GetInt("--seeds", 50),
            MaxIterations: GetInt("--max-iterations", 20_000),
            NaiveTrials: GetInt("--naive-trials", 5),
            NaiveMaxRuns: GetInt("--naive-max-runs", 20_000),
            NaiveBudgetMs: GetInt("--naive-budget-ms", 5_000),
            Out: Get("--out"),
            Only: Get("--only"),
            Sweep: !args.Contains("--no-sweep"),
            SweepSeeds: GetInt("--sweep-seeds", 20),
            SweepNaiveBudgetMs: GetInt("--sweep-naive-budget-ms", 2_000));
    }
}
