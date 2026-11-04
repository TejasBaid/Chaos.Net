namespace ChaosNet.Tests;

public sealed class BugDetectionTests
{
    private static TestReport Run(Func<Task> test, TestConfig? config = null) =>
        TestEngine.Run(test, (config ?? new TestConfig()) with { Strategy = config?.Strategy ?? Strategies.Random(1) });

    [Fact]
    public void Assertion_failure_is_reported_with_message()
    {
        TestReport report = Run(TestPrograms.LostUpdate);
        BugReport bug = Assert.IsType<BugReport>(report.FirstBug);
        Assert.Equal(BugKind.AssertionFailure, bug.Kind);
        Assert.Contains("lost update: counter = 1", bug.Message, StringComparison.Ordinal);
        Assert.True(bug.ReplayVerified);
    }

    [Fact]
    public void Assertion_failure_cannot_be_swallowed_by_the_code_under_test()
    {
        TestReport report = Run(async () =>
        {
            await ControlledTask.Run(() =>
            {
                try
                {
                    Chaos.Assert(1 + 1 == 3, "math is broken");
                }
                catch (ChaosAssertionException)
                {
                    // swallowed
                }

                return Task.CompletedTask;
            });
        });
        Assert.Equal(BugKind.AssertionFailure, report.FirstBug?.Kind);
        Assert.Equal("math is broken", report.FirstBug!.Message);
    }

    [Fact]
    public void Assert_reports_the_failing_expression_when_no_message_is_given()
    {
        int x = 41;
        TestReport report = Run(() =>
        {
            Chaos.Assert(x == 42);
            return Task.CompletedTask;
        });
        Assert.Contains("x == 42", report.FirstBug!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unhandled_exception_in_root_is_reported()
    {
        TestReport report = Run(async () =>
        {
            await Chaos.Interleave();
            throw new InvalidOperationException("boom");
        });
        Assert.Equal(BugKind.UnhandledException, report.FirstBug?.Kind);
        Assert.IsType<InvalidOperationException>(report.FirstBug!.Exception);
    }

    [Fact]
    public void Unhandled_exception_in_background_operation_is_reported_even_if_never_awaited()
    {
        TestReport report = Run(() =>
        {
            _ = ControlledTask.Run(async () =>
            {
                await Chaos.Interleave();
                throw new FormatException("bad payload");
            }, "fire-and-forget");
            return Task.CompletedTask;
        });
        Assert.Equal(BugKind.UnhandledException, report.FirstBug?.Kind);
        Assert.Contains("fire-and-forget", report.FirstBug!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exception_in_async_void_is_reported()
    {
        static async void Handler()
        {
            await Chaos.Interleave();
            throw new ArgumentException("event handler failed");
        }

        TestReport report = Run(async () =>
        {
            Handler();
            await Chaos.Interleave();
            await Chaos.Interleave();
        });
        Assert.Equal(BugKind.UnhandledException, report.FirstBug?.Kind);
        Assert.Contains("async void", report.FirstBug!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lock_ordering_deadlock_is_reported_with_the_wait_for_cycle()
    {
        TestReport report = Run(async () =>
        {
            var l1 = new AsyncLock("L1");
            var l2 = new AsyncLock("L2");
            Task a = ControlledTask.Run(async () =>
            {
                using (await l1.AcquireAsync())
                using (await l2.AcquireAsync())
                {
                }
            }, "A");
            Task b = ControlledTask.Run(async () =>
            {
                using (await l2.AcquireAsync())
                using (await l1.AcquireAsync())
                {
                }
            }, "B");
            await Task.WhenAll(a, b);
        }, new TestConfig { Iterations = 1000 });

        BugReport bug = Assert.IsType<BugReport>(report.FirstBug);
        Assert.Equal(BugKind.Deadlock, bug.Kind);
        Assert.Contains("op1 'A' is blocked on AsyncLock 'L2' (held by op2 'B')", bug.Message, StringComparison.Ordinal);
        Assert.Contains("op2 'B' is blocked on AsyncLock 'L1' (held by op1 'A')", bug.Message, StringComparison.Ordinal);
        Assert.True(bug.ReplayVerified);
    }

    [Fact]
    public void Operation_stuck_on_a_lock_after_the_test_body_completes_is_a_deadlock()
    {
        TestReport report = Run(async () =>
        {
            var gate = new AsyncLock("leaked");
            await gate.AcquireAsync(); // never released
            _ = ControlledTask.Run(async () =>
            {
                using (await gate.AcquireAsync())
                {
                }
            }, "victim");
        });
        Assert.Equal(BugKind.Deadlock, report.FirstBug?.Kind);
        Assert.Contains("test body completed", report.FirstBug!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Idle_receive_loop_after_the_test_body_completes_is_not_a_deadlock()
    {
        TestReport report = Run(async () =>
        {
            var inbox = new ControlledChannel<int>(name: "inbox");
            _ = ControlledTask.Run(async () =>
            {
                while (true)
                {
                    _ = await inbox.ReceiveAsync();
                }
            }, "server");
            await inbox.SendAsync(1);
            await inbox.SendAsync(2);
        });
        Assert.False(report.HasBugs, report.ToString());
    }

    [Fact]
    public void Livelock_is_reported_when_the_step_bound_is_hit()
    {
        TestReport report = Run(async () =>
        {
            var turn = new SharedRegister<int>(0, "turn");
            async Task Polite(int me)
            {
                // Each side waits until it is "its turn", but nobody ever hands the turn over.
                while (await turn.ReadAsync() != me)
                {
                    await Chaos.Interleave();
                }
            }

            await Task.WhenAll(ControlledTask.Run(() => Polite(1)), ControlledTask.Run(() => Polite(2)));
        }, new TestConfig { Iterations = 5, MaxSteps = 500 });
        Assert.Equal(BugKind.Livelock, report.FirstBug?.Kind);
        Assert.Equal(500, report.FirstBug!.Steps);
        Assert.True(report.FirstBug.ReplayVerified);
    }

    [Fact]
    public void Spin_wait_under_unfair_PCT_is_not_a_false_livelock()
    {
        // With PCT the spinner may get the highest priority and starve the setter. After MaxUnfairSteps the engine
        // falls back to a fair random walk, so the setter eventually runs and the test terminates.
        TestReport report = TestEngine.Run(async () =>
        {
            var flag = new SharedRegister<bool>(false, "flag");
            Task spinner = ControlledTask.Run(async () =>
            {
                while (!await flag.ReadAsync())
                {
                }
            }, "spinner");
            Task setter = ControlledTask.Run(async () =>
            {
                await Chaos.Interleave();
                await flag.WriteAsync(true);
            }, "setter");
            await Task.WhenAll(spinner, setter);
        }, new TestConfig { Iterations = 200, Strategy = Strategies.Pct(1, 5), MaxSteps = 3000, MaxUnfairSteps = 500 });
        Assert.False(report.HasBugs, report.ToString());
        Assert.True(report.MaxStepsInIteration > 500, "some iteration should have been starved until the fairness fallback");
    }

    private sealed class RequestMonitor : ChaosMonitor
    {
        public void Requested() => Hot("request pending");

        public void Served() => Cold();
    }

    [Fact]
    public void Liveness_monitor_hot_at_the_end_is_a_liveness_violation()
    {
        TestReport report = Run(async () =>
        {
            var monitor = Chaos.Monitor<RequestMonitor>();
            var requests = new ControlledChannel<int>(name: "requests");
            _ = ControlledTask.Run(async () =>
            {
                while (true)
                {
                    int r = await requests.ReceiveAsync();
                    if (r % 2 == 0 || Chaos.RandomBool())
                    {
                        monitor.Served(); // BUG: odd requests are sometimes silently dropped
                    }
                }
            }, "server");
            monitor.Requested();
            await requests.SendAsync(1);
        }, new TestConfig { Iterations = 100 });
        Assert.Equal(BugKind.LivenessViolation, report.FirstBug?.Kind);
        Assert.Contains("request pending", report.FirstBug!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Liveness_temperature_threshold_catches_starvation_in_a_non_terminating_system()
    {
        TestReport report = Run(async () =>
        {
            var monitor = Chaos.Monitor<RequestMonitor>();
            monitor.Requested();
            while (true)
            {
                await ControlledTask.Delay(10); // a heartbeat loop that never serves the request
            }
        }, new TestConfig { Iterations = 3, MaxSteps = 5000, ReportLivelocks = false, LivenessTemperatureThreshold = 100 });
        Assert.Equal(BugKind.LivenessViolation, report.FirstBug?.Kind);
        Assert.Contains("more than 100 steps", report.FirstBug!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Task_Run_inside_the_test_is_reported_as_uncontrolled_concurrency()
    {
#pragma warning disable CHAOS001
        TestReport report = Run(async () => await Task.Run(async () =>
        {
            await Chaos.Interleave();
        }), new TestConfig { Iterations = 3 });
#pragma warning restore CHAOS001
        Assert.Equal(BugKind.UncontrolledConcurrency, report.FirstBug?.Kind);
    }

    [Fact]
    public void ConfigureAwait_false_escape_is_reported()
    {
        TestReport report = Run(async () =>
        {
            await ControlledTask.Delay(5).ConfigureAwait(false);
            await Chaos.Interleave(); // now on a thread-pool thread
        }, new TestConfig { Iterations = 3 });
        Assert.Equal(BugKind.UncontrolledConcurrency, report.FirstBug?.Kind);
    }

    [Fact]
    public void Real_Task_Delay_is_reported_instead_of_a_misleading_deadlock()
    {
        // A generous grace period keeps this robust when the thread pool is busy with other tests.
        TestReport report = Run(async () => await Task.Delay(1), new TestConfig { Iterations = 2, UncontrolledGracePeriod = TimeSpan.FromSeconds(5) });
        Assert.Equal(BugKind.UncontrolledConcurrency, report.FirstBug?.Kind);
    }

    [Fact]
    public void Tolerate_policy_accepts_uncontrolled_work_with_a_warning()
    {
        TestReport report = Run(async () => await Task.Delay(1), new TestConfig
        {
            Iterations = 3,
            UncontrolledConcurrency = UncontrolledConcurrencyPolicy.Tolerate,
            UncontrolledGracePeriod = TimeSpan.FromSeconds(2),
        });
        Assert.False(report.HasBugs, report.ToString());
        Assert.NotEmpty(report.Warnings);
    }

    [Fact]
    public void Blocking_the_scheduler_thread_is_reported_as_a_hang()
    {
        TestReport report = Run(() =>
        {
#pragma warning disable CHAOS005
            ControlledTask.Run(async () => await Chaos.Interleave()).Wait(); // deadlocks the single scheduler thread
#pragma warning restore CHAOS005
            return Task.CompletedTask;
        }, new TestConfig { Iterations = 1, IterationTimeout = TimeSpan.FromMilliseconds(300) });
        Assert.Equal(BugKind.Hang, report.FirstBug?.Kind);
    }

    [Fact]
    public void Run_with_Action_overload_works()
    {
        int ran = 0;
        TestReport report = TestEngine.Run(() => { ran++; }, new TestConfig { Iterations = 3, Strategy = Strategies.Random(1) });
        Assert.False(report.HasBugs);
        Assert.Equal(3, ran);
    }

    [Fact]
    public void StopOnFirstBug_false_collects_many_buggy_iterations()
    {
        TestReport report = TestEngine.Run(TestPrograms.LostUpdate, new TestConfig
        {
            Iterations = 200,
            Strategy = Strategies.Random(4),
            StopOnFirstBug = false,
            VerifyBugsByReplay = false,
            MaxBugsReported = 3,
        });
        Assert.Equal(200, report.Iterations);
        Assert.True(report.BuggyIterations > 10);
        Assert.Equal(3, report.Bugs.Count);
    }
}
