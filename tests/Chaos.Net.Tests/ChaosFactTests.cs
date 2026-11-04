using ChaosNet.Samples;
using ChaosNet.Xunit;

namespace ChaosNet.Tests;

/// <summary>Tests written with the [ChaosFact] xUnit integration.</summary>
public sealed class ChaosFactTests
{
    private int _calls;

    [ChaosFact(Iterations = 300, Seed = 42)]
    public Task Fixed_bank_conserves_money() => BankTransferSample.FixedTest();

    [ChaosFact(Iterations = 5000, Strategy = ChaosStrategy.Pct, PctDepth = 3, Seed = 1, ExpectBug = true, ExpectedBug = BugKind.AssertionFailure)]
    public Task Depth3_checkpoint_bug_is_found() => CheckpointSample.BuggyTest();

    [ChaosFact(Iterations = 1000, Strategy = ChaosStrategy.Dfs, ExpectBug = true, ExpectedBug = BugKind.Deadlock)]
    public Task Producer_consumer_deadlock_is_found_by_dfs() => ProducerConsumerSample.BuggyTest();

    [ChaosFact(Iterations = 1000, Strategy = ChaosStrategy.Dfs, PreemptionBound = 2)]
    public Task Fixed_checkpoint_passes_context_bounded_dfs() => CheckpointSample.RunAsync(buggy: false, cellsPerTransaction: 2);

    [ChaosFact(Iterations = 50, Seed = 3)]
    public async Task Each_iteration_runs_on_a_fresh_test_class_instance()
    {
        _calls++;
        Chaos.Assert(_calls == 1, "the test class instance was reused across iterations");
        await Chaos.Interleave();
    }

    [ChaosFact(Iterations = 20, Seed = 4)]
    public void Synchronous_test_methods_are_supported()
    {
        _ = ControlledTask.Run(async () =>
        {
            await Chaos.Interleave();
            Chaos.Assert(Chaos.IsControlled);
        });
    }

    [Fact]
    public void ChaosAssert_helpers_fail_with_the_report()
    {
        TestReport buggy = TestEngine.Run(TestPrograms.LostUpdate, new TestConfig { Iterations = 100, Strategy = Strategies.Random(1) });
        BugReport bug = ChaosAssert.FindsBug(buggy, BugKind.AssertionFailure);
        Assert.Equal(BugKind.AssertionFailure, bug.Kind);
        var ex = Assert.Throws<global::Xunit.Sdk.XunitException>(() => ChaosAssert.NoBugs(buggy));
        Assert.Contains("lost update", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ChaosTestFailedException>(buggy.ThrowIfBug);
    }
}
