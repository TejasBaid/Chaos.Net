using Xunit.Sdk;

namespace ChaosNet.Xunit;

/// <summary>xUnit assertions over <see cref="TestReport"/>s.</summary>
public static class ChaosAssert
{
    /// <summary>Fails the test with the full bug report if <paramref name="report"/> contains a bug.</summary>
    public static void NoBugs(TestReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.HasBugs)
        {
            throw new XunitException("Chaos.Net found a bug.\n" + report);
        }
    }

    /// <summary>Fails the test unless <paramref name="report"/> contains a bug (optionally of kind <paramref name="kind"/>).</summary>
    public static BugReport FindsBug(TestReport report, BugKind? kind = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.FirstBug is not { } bug)
        {
            throw new XunitException($"Expected Chaos.Net to find a {kind?.ToString() ?? "bug"}, but it found none.\n{report.Summary()}");
        }

        if (kind is { } k && bug.Kind != k)
        {
            throw new XunitException($"Expected a {k} but Chaos.Net found a {bug.Kind}.\n{report}");
        }

        return bug;
    }

    /// <summary>Runs <paramref name="test"/> under <paramref name="config"/> and fails with the bug report if a bug is found.</summary>
    public static TestReport Run(Func<Task> test, TestConfig? config = null)
    {
        TestReport report = TestEngine.Run(test, config);
        NoBugs(report);
        return report;
    }

    internal static void Check(TestReport report, bool expectBug, BugKind expected)
    {
        if (!expectBug)
        {
            NoBugs(report);
        }
        else
        {
            FindsBug(report, (int)expected < 0 ? null : expected);
        }
    }
}
