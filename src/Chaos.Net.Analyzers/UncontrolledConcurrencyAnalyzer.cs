using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ChaosNet.Analyzers;

/// <summary>
/// Flags constructs that the Chaos.Net scheduler cannot control: they either run work outside the controlled
/// scheduler, block the scheduler thread, or introduce nondeterminism that replay cannot reproduce.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UncontrolledConcurrencyAnalyzer : DiagnosticAnalyzer
{
    private const string Category = "Chaos.Net";

    /// <summary>CHAOS001: work started outside the controlled scheduler.</summary>
    public static readonly DiagnosticDescriptor UncontrolledTask = new(
        "CHAOS001", "Uncontrolled concurrency",
        "'{0}' runs work outside the Chaos.Net scheduler; use ControlledTask.Run so the interleaving can be explored and replayed",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    /// <summary>CHAOS002: real time used for waiting.</summary>
    public static readonly DiagnosticDescriptor UncontrolledDelay = new(
        "CHAOS002", "Uncontrolled time",
        "'{0}' waits on real time; use ControlledTask.Delay or pass Chaos.TimeProvider so the delay runs on virtual time",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    /// <summary>CHAOS003: ConfigureAwait(false) escapes the controlled context.</summary>
    public static readonly DiagnosticDescriptor ConfigureAwaitFalse = new(
        "CHAOS003", "Continuation escapes the scheduler",
        "ConfigureAwait(false) sends the continuation to the thread pool, outside the Chaos.Net scheduler; remove it from code under test",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    /// <summary>CHAOS004: blocking synchronization primitive.</summary>
    public static readonly DiagnosticDescriptor BlockingSynchronization = new(
        "CHAOS004", "Blocking synchronization",
        "'{0}' can block the thread; under test this freezes the single scheduler thread. Use AsyncLock / ControlledSemaphore (a plain lock statement is fine).",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    /// <summary>CHAOS005: synchronously blocking on a task.</summary>
    public static readonly DiagnosticDescriptor BlockingOnTask = new(
        "CHAOS005", "Synchronous wait on a task",
        "'{0}' blocks on a task; under test this hangs the scheduler thread. Use await.",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    /// <summary>CHAOS006: uncontrolled nondeterminism.</summary>
    public static readonly DiagnosticDescriptor UncontrolledNondeterminism = new(
        "CHAOS006", "Uncontrolled nondeterminism",
        "'{0}' is nondeterministic and cannot be replayed; use Chaos.RandomInt/RandomBool or Chaos.TimeProvider",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        UncontrolledTask, UncontrolledDelay, ConfigureAwaitFalse, BlockingSynchronization, BlockingOnTask, UncontrolledNondeterminism);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var types = new KnownTypes(start.Compilation);
            start.RegisterOperationAction(c => AnalyzeInvocation(c, types), OperationKind.Invocation);
            start.RegisterOperationAction(c => AnalyzeCreation(c, types), OperationKind.ObjectCreation);
            start.RegisterOperationAction(c => AnalyzeProperty(c, types), OperationKind.PropertyReference);

            // The C# lock statement is deliberately not reported: its body cannot contain await, so under test it is
            // confined to a single scheduling step and can never block the scheduler thread, while in production it
            // provides the atomicity the controlled model assumes. Monitor.Enter/Wait (which can span awaits or block
            // indefinitely) are reported below.
        });
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, KnownTypes t)
    {
        var invocation = (IInvocationOperation)context.Operation;
        IMethodSymbol method = invocation.TargetMethod;
        INamedTypeSymbol type = method.ContainingType;
        string name = method.Name;
        string display = type.Name + "." + name;

        if ((Is(type, t.Task) && name == "Run") ||
            (Is(type, t.TaskFactory) && name == "StartNew" && !HasArgumentOfType(invocation, t.TaskScheduler)) ||
            (Is(type, t.ThreadPool) && name.EndsWith("QueueUserWorkItem", System.StringComparison.Ordinal)) ||
            (Is(type, t.Parallel) && (name == "For" || name == "ForEach" || name == "Invoke" || name == "ForAsync" || name == "ForEachAsync")))
        {
            Report(context, UncontrolledTask, invocation, display);
        }
        else if ((Is(type, t.Task) && name == "Delay" && !HasArgumentOfType(invocation, t.TimeProvider)) || (Is(type, t.Thread) && name == "Sleep"))
        {
            Report(context, UncontrolledDelay, invocation, display);
        }
        else if (name == "ConfigureAwait" && (Is(type, t.Task) || Is(type, t.TaskOfT) || Is(type, t.ValueTask) || Is(type, t.ValueTaskOfT)) && ContinuesOffContext(invocation))
        {
            context.ReportDiagnostic(Diagnostic.Create(ConfigureAwaitFalse, invocation.Syntax.GetLocation()));
        }
        else if ((Is(type, t.Monitor) && (name == "Enter" || name == "TryEnter" || name == "Wait")) ||
                 (Is(type, t.SemaphoreSlim) && name == "Wait") ||
                 (Is(type, t.ManualResetEventSlim) && name == "Wait") ||
                 (Is(type, t.WaitHandle, inherit: true) && name.StartsWith("Wait", System.StringComparison.Ordinal)))
        {
            Report(context, BlockingSynchronization, invocation, display);
        }
        else if (((Is(type, t.Task) || Is(type, t.TaskOfT)) && (name == "Wait" || name == "WaitAll" || name == "WaitAny")) ||
                 (name == "GetResult" && (Is(type, t.TaskAwaiter) || Is(type, t.TaskAwaiterOfT))))
        {
            Report(context, BlockingOnTask, invocation, name == "GetResult" ? "GetAwaiter().GetResult()" : display);
        }
        else if (Is(type, t.Guid) && name == "NewGuid")
        {
            // Identity generation is usually harmless; not reported.
        }
    }

    private static void AnalyzeCreation(OperationAnalysisContext context, KnownTypes t)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        INamedTypeSymbol? type = creation.Type as INamedTypeSymbol;
        if (type is null)
        {
            return;
        }

        if (Is(type, t.Thread) || Is(type, t.Timer))
        {
            Report(context, UncontrolledTask, creation, "new " + type.Name);
        }
        else if (Is(type, t.Random))
        {
            Report(context, UncontrolledNondeterminism, creation, "new Random");
        }
    }

    private static void AnalyzeProperty(OperationAnalysisContext context, KnownTypes t)
    {
        var reference = (IPropertyReferenceOperation)context.Operation;
        IPropertySymbol property = reference.Property;
        INamedTypeSymbol type = property.ContainingType;
        string name = property.Name;
        if ((Is(type, t.DateTime) || Is(type, t.DateTimeOffset)) && (name == "Now" || name == "UtcNow" || name == "Today"))
        {
            Report(context, UncontrolledNondeterminism, reference, type.Name + "." + name);
        }
        else if ((Is(type, t.Environment) && (name == "TickCount" || name == "TickCount64")) || (Is(type, t.Random) && name == "Shared"))
        {
            Report(context, UncontrolledNondeterminism, reference, type.Name + "." + name);
        }
        else if (Is(type, t.TaskOfT) && name == "Result")
        {
            Report(context, BlockingOnTask, reference, "Task.Result");
        }
    }

    private static bool ContinuesOffContext(IInvocationOperation invocation)
    {
        if (invocation.Arguments.Length != 1)
        {
            return false;
        }

        Optional<object?> value = invocation.Arguments[0].Value.ConstantValue;
        if (!value.HasValue)
        {
            return false;
        }

        return value.Value switch
        {
            bool b => !b,
            int options => (options & 1) == 0, // ConfigureAwaitOptions.ContinueOnCapturedContext == 1
            _ => false,
        };
    }

    private static bool HasArgumentOfType(IInvocationOperation invocation, INamedTypeSymbol? type) =>
        type is not null && invocation.Arguments.Any(a => SymbolEqualityComparer.Default.Equals(a.Parameter?.Type, type));

    private static bool Is(INamedTypeSymbol? type, INamedTypeSymbol? expected, bool inherit = false)
    {
        if (type is null || expected is null)
        {
            return false;
        }

        for (INamedTypeSymbol? current = type; current is not null; current = inherit ? current.BaseType : null)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, expected))
            {
                return true;
            }
        }

        return false;
    }

    private static void Report(OperationAnalysisContext context, DiagnosticDescriptor descriptor, IOperation operation, string what) =>
        context.ReportDiagnostic(Diagnostic.Create(descriptor, operation.Syntax.GetLocation(), what));

    private sealed class KnownTypes
    {
        public KnownTypes(Compilation c)
        {
            Task = c.GetTypeByMetadataName("System.Threading.Tasks.Task");
            TaskOfT = c.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
            ValueTask = c.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");
            ValueTaskOfT = c.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
            TaskFactory = c.GetTypeByMetadataName("System.Threading.Tasks.TaskFactory");
            TaskScheduler = c.GetTypeByMetadataName("System.Threading.Tasks.TaskScheduler");
            Parallel = c.GetTypeByMetadataName("System.Threading.Tasks.Parallel");
            TaskAwaiter = c.GetTypeByMetadataName("System.Runtime.CompilerServices.TaskAwaiter");
            TaskAwaiterOfT = c.GetTypeByMetadataName("System.Runtime.CompilerServices.TaskAwaiter`1");
            ThreadPool = c.GetTypeByMetadataName("System.Threading.ThreadPool");
            Thread = c.GetTypeByMetadataName("System.Threading.Thread");
            Timer = c.GetTypeByMetadataName("System.Threading.Timer");
            Monitor = c.GetTypeByMetadataName("System.Threading.Monitor");
            SemaphoreSlim = c.GetTypeByMetadataName("System.Threading.SemaphoreSlim");
            ManualResetEventSlim = c.GetTypeByMetadataName("System.Threading.ManualResetEventSlim");
            WaitHandle = c.GetTypeByMetadataName("System.Threading.WaitHandle");
            TimeProvider = c.GetTypeByMetadataName("System.TimeProvider");
            DateTime = c.GetTypeByMetadataName("System.DateTime");
            DateTimeOffset = c.GetTypeByMetadataName("System.DateTimeOffset");
            Environment = c.GetTypeByMetadataName("System.Environment");
            Random = c.GetTypeByMetadataName("System.Random");
            Guid = c.GetTypeByMetadataName("System.Guid");
        }

        public INamedTypeSymbol? Task { get; }
        public INamedTypeSymbol? TaskOfT { get; }
        public INamedTypeSymbol? ValueTask { get; }
        public INamedTypeSymbol? ValueTaskOfT { get; }
        public INamedTypeSymbol? TaskFactory { get; }
        public INamedTypeSymbol? TaskScheduler { get; }
        public INamedTypeSymbol? Parallel { get; }
        public INamedTypeSymbol? TaskAwaiter { get; }
        public INamedTypeSymbol? TaskAwaiterOfT { get; }
        public INamedTypeSymbol? ThreadPool { get; }
        public INamedTypeSymbol? Thread { get; }
        public INamedTypeSymbol? Timer { get; }
        public INamedTypeSymbol? Monitor { get; }
        public INamedTypeSymbol? SemaphoreSlim { get; }
        public INamedTypeSymbol? ManualResetEventSlim { get; }
        public INamedTypeSymbol? WaitHandle { get; }
        public INamedTypeSymbol? TimeProvider { get; }
        public INamedTypeSymbol? DateTime { get; }
        public INamedTypeSymbol? DateTimeOffset { get; }
        public INamedTypeSymbol? Environment { get; }
        public INamedTypeSymbol? Random { get; }
        public INamedTypeSymbol? Guid { get; }
    }
}
