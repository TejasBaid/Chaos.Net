using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using ChaosNet.Runtime;
using ChaosNet.Scheduling;
using ChaosNet.Tracing;

namespace ChaosNet;

/// <summary>
/// Runs a test body many times under controlled scheduling, exploring a different interleaving each iteration, and
/// reports the first (or every) bug together with a replayable trace.
/// </summary>
/// <example>
/// <code>
/// TestReport report = TestEngine.Run(async () =>
/// {
///     var account = new SharedRegister&lt;int&gt;(100, "balance");
///     Task a = ControlledTask.Run(() => WithdrawAsync(account, 60));
///     Task b = ControlledTask.Run(() => WithdrawAsync(account, 60));
///     await Task.WhenAll(a, b);
///     Chaos.Assert(account.Value >= 0, "overdraft");
/// }, new TestConfig { Iterations = 10_000, Strategy = Strategies.Pct(depth: 3) });
/// report.ThrowIfBug();
/// </code>
/// </example>
/// <remarks>
/// The test body must create its state on each invocation: it runs once per iteration and once more when a bug is
/// replayed for verification.
/// </remarks>
public static class TestEngine
{
    private const int SchedulerStackSize = 16 * 1024 * 1024;

    /// <summary>Runs <paramref name="test"/> under the configured strategy.</summary>
    public static TestReport Run(Func<Task> test, TestConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(test);
        config ??= new TestConfig();
        var session = new Session(test, config, config.Strategy ?? Strategies.Random(), recordAll: false, replayOf: null);
        return Execute(session);
    }

    /// <summary>Runs a synchronous <paramref name="test"/> (which typically starts operations with <see cref="ControlledTask.Run(Action, string?)"/>).</summary>
    public static TestReport Run(Action test, TestConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(test);
        return Run(() =>
        {
            test();
            return Task.CompletedTask;
        }, config);
    }

    /// <summary>Runs <paramref name="test"/> without blocking the caller.</summary>
    public static Task<TestReport> RunAsync(Func<Task> test, TestConfig? config = null) =>
        Task.Factory.StartNew(() => Run(test, config), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>
    /// Re-executes <paramref name="test"/> on the exact schedule recorded in <paramref name="trace"/>, with full
    /// event recording. <see cref="TestReport.Reproduced"/> tells whether the same bug occurred on the same schedule.
    /// </summary>
    public static TestReport Replay(Func<Task> test, ScheduleTrace trace, TimeSpan? iterationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(test);
        ArgumentNullException.ThrowIfNull(trace);
        TestConfig config = new TestConfig
        {
            Iterations = 1,
            VerifyBugsByReplay = false,
            TestName = trace.TestName,
            IterationTimeout = iterationTimeout ?? TimeSpan.FromSeconds(30),
        }.WithTraceSettings(trace.Settings);
        var session = new Session(test, config, new ReplayStrategy(trace), recordAll: true, replayOf: trace);
        return Execute(session);
    }

    /// <summary>Replays the trace stored at <paramref name="tracePath"/>. See <see cref="Replay(Func{Task}, ScheduleTrace, TimeSpan?)"/>.</summary>
    public static TestReport Replay(Func<Task> test, string tracePath) => Replay(test, ScheduleTrace.Load(tracePath));

    private static TestReport Execute(Session session)
    {
        TestReport? report = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                report = session.Explore();
            }
            catch (Exception e)
            {
                failure = e;
            }
        }, SchedulerStackSize)
        {
            IsBackground = true,
            Name = "Chaos.Net scheduler",
        };

        thread.Start();
        while (!thread.Join(25))
        {
            long started = Volatile.Read(ref session.IterationStartedTimestamp);
            if (started != 0 && Stopwatch.GetElapsedTime(started) > session.Config.IterationTimeout)
            {
                // The scheduler thread is stuck inside user code; abandon it (it is a background thread).
                return session.BuildHangReport();
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        return report!;
    }

    private sealed class Session(Func<Task> test, TestConfig config, ISchedulingStrategy strategy, bool recordAll, ScheduleTrace? replayOf)
    {
        public long IterationStartedTimestamp;
        private readonly Stopwatch _stopwatch = new();
        private readonly List<BugReport> _bugs = [];
        private readonly List<string> _warnings = [];
        private readonly HashSet<ulong> _schedules = [];
        private volatile ChaosRuntime? _currentRuntime;
        private int _currentIteration;
        private int _iterations;
        private int _buggy;
        private long _totalSteps;
        private int _maxSteps;
        private int _boundHits;

        public TestConfig Config => config;

        public TestReport Explore()
        {
            _stopwatch.Start();
            bool exhausted = false;
            bool? reproduced = null;
            string? replayTimeline = null;
            for (int i = 0; i < config.Iterations; i++)
            {
                if (config.TimeLimit is { } limit && _stopwatch.Elapsed > limit)
                {
                    break;
                }

                if (!strategy.PrepareIteration(i))
                {
                    exhausted = true;
                    break;
                }

                string strategyDescription = strategy.ToString() ?? strategy.Name;
                ulong? seed = strategy.CurrentIterationSeed;
                string? reproduction = strategy.DescribeReproduction();
                var runtime = new ChaosRuntime(config, strategy, recordAll);
                _currentRuntime = runtime;
                _currentIteration = i;
                Volatile.Write(ref IterationStartedTimestamp, Stopwatch.GetTimestamp());
                IterationResult result = runtime.Run(test);
                Volatile.Write(ref IterationStartedTimestamp, 0);
                _currentRuntime = null;

                strategy.CompleteIteration(new IterationOutcome(i, result.Steps, result.Bug is not null));
                _iterations++;
                _totalSteps += result.Steps;
                _maxSteps = Math.Max(_maxSteps, result.Steps);
                if (result.HitStepBound)
                {
                    _boundHits++;
                }

                ulong hash = ScheduleCodec.Hash(result.Decisions);
                _schedules.Add(hash);
                foreach (string w in result.Warnings)
                {
                    if (_warnings.Count < 50)
                    {
                        _warnings.Add(w);
                    }
                }

                config.OnIterationCompleted?.Invoke(new IterationSummary(i, result.Steps, hash, result.Decisions, result.Bug?.Kind, seed));

                if (replayOf is not null)
                {
                    replayTimeline = Render(result);
                    reproduced = result.Bug is { } b && b.Kind == replayOf.BugKind && result.Decisions.SequenceEqual(replayOf.Decisions);
                }

                if (result.Bug is not null)
                {
                    _buggy++;
                    if (_bugs.Count < config.MaxBugsReported)
                    {
                        _bugs.Add(CreateBugReport(i, result, strategyDescription, seed, reproduction));
                    }

                    if (config.StopOnFirstBug)
                    {
                        break;
                    }
                }
            }

            if (strategy is DfsStrategy { IsExhausted: true })
            {
                exhausted = true;
            }

            _stopwatch.Stop();
            return BuildReport(exhausted, reproduced, replayTimeline);
        }

        private TestReport BuildReport(bool exhausted, bool? reproduced, string? timeline) => new()
        {
            TestName = config.TestName,
            Strategy = replayOf is not null ? $"replay of {replayOf.Strategy}" : strategy.ToString() ?? strategy.Name,
            Iterations = _iterations,
            BuggyIterations = _buggy,
            Bugs = [.. _bugs],
            Elapsed = _stopwatch.Elapsed,
            TotalSteps = _totalSteps,
            MaxStepsInIteration = _maxSteps,
            DistinctSchedules = _schedules.Count,
            IterationsHitStepBound = _boundHits,
            SearchExhausted = exhausted,
            Warnings = [.. _warnings],
            Reproduced = reproduced,
            Timeline = timeline,
        };

        private static string Render(IterationResult result) =>
            result.Timeline is null
                ? string.Empty
                : TimelineRenderer.Render(result.Timeline, result.Operations,
                    result.Bug is { } bug ? $"BUG at step {bug.Step}: {bug.Kind}: {bug.Message}" : "No bug occurred in this execution.");

        private ScheduleTrace CreateTrace(int iteration, IterationResult result, string strategyDescription, ulong? seed, string? reproduction, string? timeline) => new()
        {
            TestName = config.TestName,
            AssemblyPath = config.TestAssemblyPath,
            AssemblyName = config.TestAssemblyPath is { } p ? Path.GetFileNameWithoutExtension(p) : null,
            Strategy = strategyDescription,
            Reproduction = reproduction,
            SeedHex = seed is { } s ? ScheduleTrace.FormatSeed(s) : null,
            Iteration = iteration,
            Settings = config.ToTraceSettings(),
            BugKind = result.Bug?.Kind,
            BugMessage = result.Bug?.Message,
            Steps = result.Steps,
            Operations = [.. result.Operations.Select(o => $"op{o.Id}: {o.Label}")],
            Schedule = ScheduleCodec.Encode(result.Decisions),
            Timeline = timeline,
        };

        private BugReport CreateBugReport(int iteration, IterationResult result, string strategyDescription, ulong? seed, string? reproduction)
        {
            BugInfo bug = result.Bug!;
            string? timeline = recordAll ? Render(result) : null;
            bool? verified = null;
            ScheduleTrace trace = CreateTrace(iteration, result, strategyDescription, seed, reproduction, timeline);

            if (config.VerifyBugsByReplay && !recordAll)
            {
                var replay = new ReplayStrategy(trace);
                replay.PrepareIteration(0);
                var runtime = new ChaosRuntime(config, replay, record: true);
                Volatile.Write(ref IterationStartedTimestamp, Stopwatch.GetTimestamp());
                IterationResult replayed = runtime.Run(test);
                Volatile.Write(ref IterationStartedTimestamp, 0);
                verified = replayed.Bug is { } rb && rb.Kind == bug.Kind && replayed.Decisions.SequenceEqual(result.Decisions);
                timeline = Render(replayed);
                trace = CreateTrace(iteration, result, strategyDescription, seed, reproduction, timeline);
            }

            var report = new BugReport(bug.Kind, bug.Message, bug.Exception, iteration, result.Steps, trace)
            {
                Timeline = timeline,
                ReplayVerified = verified,
            };

            if (config.TraceDirectory is { } dir)
            {
                string name = Sanitize(config.TestName ?? "chaos-test");
                string file = Path.Combine(dir, string.Create(CultureInfo.InvariantCulture,
                    $"{name}.{bug.Kind}.iter{iteration}.{DateTime.UtcNow:yyyyMMddTHHmmssfff}.chaos.json"));
                trace.Save(file);
                report.TracePath = Path.GetFullPath(file);
            }

            return report;
        }

        private static string Sanitize(string name)
        {
            return new string([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')]);
        }

        public TestReport BuildHangReport()
        {
            ChaosRuntime? runtime = _currentRuntime;
            string message =
                $"Iteration {_currentIteration} ran longer than {config.IterationTimeout.TotalSeconds:0.#}s of wall-clock time. The scheduler thread is blocked " +
                "inside the code under test: a synchronous wait on a controlled task (.Result, .Wait(), GetAwaiter().GetResult()), Thread.Sleep, a blocking lock, " +
                "or an infinite loop without a scheduling point. Controlled code must stay asynchronous.";
            var trace = new ScheduleTrace
            {
                TestName = config.TestName,
                Strategy = strategy.ToString() ?? strategy.Name,
                Iteration = _currentIteration,
                Settings = config.ToTraceSettings(),
                BugKind = BugKind.Hang,
                BugMessage = message,
                Steps = runtime?.Step ?? 0,
            };
            _bugs.Add(new BugReport(BugKind.Hang, message, null, _currentIteration, runtime?.Step ?? 0, trace));
            _buggy++;
            return BuildReport(exhausted: false, reproduced: null, timeline: null);
        }
    }
}
