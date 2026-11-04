; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
CHAOS001 | Chaos.Net | Warning | Uncontrolled concurrency (Task.Run, StartNew, ThreadPool, Thread, Timer, Parallel)
CHAOS002 | Chaos.Net | Warning | Uncontrolled time (Task.Delay without TimeProvider, Thread.Sleep)
CHAOS003 | Chaos.Net | Warning | ConfigureAwait(false) escapes the scheduler
CHAOS004 | Chaos.Net | Warning | Blocking synchronization (Monitor, SemaphoreSlim.Wait, WaitHandle)
CHAOS005 | Chaos.Net | Warning | Synchronous wait on a task (.Result, .Wait(), GetResult())
CHAOS006 | Chaos.Net | Warning | Uncontrolled nondeterminism (DateTime.Now, Random, TickCount)
