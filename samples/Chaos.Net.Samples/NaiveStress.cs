using System.Diagnostics;

namespace ChaosNet.Samples;

/// <summary>Outcome of a naive stress run.</summary>
/// <param name="Runs">Executions performed.</param>
/// <param name="Failures">Executions that threw (assertion or other exception).</param>
/// <param name="Hangs">Executions that did not finish within the per-run timeout (deadlock or livelock).</param>
/// <param name="FirstFailureRun">One-based index of the first failing run, if any.</param>
/// <param name="FirstFailure">Description of the first failure, if any.</param>
/// <param name="Elapsed">Wall-clock time spent.</param>
public sealed record NaiveStressResult(int Runs, int Failures, int Hangs, int? FirstFailureRun, string? FirstFailure, TimeSpan Elapsed)
{
    /// <summary>Gets whether any run failed or hung.</summary>
    public bool FoundBug => Failures + Hangs > 0;
}

/// <summary>
/// The baseline Chaos.Net is compared against: run the system for real, on the thread pool, over and over, and hope
/// the OS scheduler produces the bad interleaving. Outside a controlled test every Chaos.Net primitive falls back to
/// its production implementation (<see cref="ControlledTask.Run(Func{Task}, string?)"/> is <see cref="Task.Run(Func{Task})"/>,
/// <see cref="AsyncLock"/> is a <see cref="SemaphoreSlim"/>, ...), so this executes exactly the code that ships.
/// </summary>
public static class NaiveStress
{
    /// <summary>Runs <paramref name="system"/> repeatedly on the thread pool.</summary>
    /// <param name="system">The system under test.</param>
    /// <param name="maxRuns">Maximum number of runs.</param>
    /// <param name="perRunTimeout">A run that takes longer is counted as a hang.</param>
    /// <param name="budget">Overall wall-clock budget.</param>
    /// <param name="stopOnFirstFailure">Stop at the first failure or hang.</param>
    public static NaiveStressResult Run(Func<Task> system, int maxRuns, TimeSpan perRunTimeout, TimeSpan budget, bool stopOnFirstFailure)
    {
        ArgumentNullException.ThrowIfNull(system);
        var sw = Stopwatch.StartNew();
        int runs = 0, failures = 0, hangs = 0;
        int? firstRun = null;
        string? first = null;
        while (runs < maxRuns && sw.Elapsed < budget)
        {
            runs++;
#pragma warning disable CHAOS001, CHAOS005 // The naive baseline deliberately uses real, uncontrolled concurrency.
            Task run = Task.Run(system);
            bool finished;
            try
            {
                finished = run.Wait(perRunTimeout);
            }
            catch (AggregateException)
            {
                finished = true;
            }
#pragma warning restore CHAOS001, CHAOS005

            string? failure = null;
            if (!finished)
            {
                hangs++;
                failure = $"hang: no progress within {perRunTimeout.TotalMilliseconds:0} ms (deadlock?)";
            }
            else if (run.IsFaulted)
            {
                failures++;
                Exception e = run.Exception!.InnerException ?? run.Exception;
                failure = $"{e.GetType().Name}: {e.Message}";
            }

            if (failure is not null && first is null)
            {
                first = failure;
                firstRun = runs;
                if (stopOnFirstFailure)
                {
                    break;
                }
            }
        }

        return new NaiveStressResult(runs, failures, hangs, firstRun, first, sw.Elapsed);
    }
}
