using System.Collections.Immutable;
using ChaosNet.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ChaosNet.Tests;

public sealed class AnalyzerTests
{
    private static async Task<string[]> AnalyzeAsync(string body)
    {
        string source = $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            public class C
            {
                private readonly object _gate = new();
                public async Task M(Task<int> t, SemaphoreSlim sem, TaskScheduler scheduler)
                {
                    {{body}}
                    await Task.CompletedTask;
                }
            }
            """;
        IEnumerable<MetadataReference> refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.Ordinal))
            .Select(p => MetadataReference.CreateFromFile(p));
        CSharpCompilation compilation = CSharpCompilation.Create("t", [CSharpSyntaxTree.ParseText(source)], refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        ImmutableArray<Diagnostic> diagnostics = await compilation.WithAnalyzers([new UncontrolledConcurrencyAnalyzer()]).GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Select(d => d.Id).Order()];
    }

    [Theory]
    [InlineData("await Task.Run(() => 1);", "CHAOS001")]
    [InlineData("await Task.Factory.StartNew(() => 1);", "CHAOS001")]
    [InlineData("ThreadPool.QueueUserWorkItem(_ => { });", "CHAOS001")]
    [InlineData("new Thread(() => { }).Start();", "CHAOS001")]
    [InlineData("using var timer = new Timer(_ => { });", "CHAOS001")]
    [InlineData("Parallel.For(0, 10, i => { });", "CHAOS001")]
    [InlineData("await Task.Delay(10);", "CHAOS002")]
    [InlineData("Thread.Sleep(10);", "CHAOS002")]
    [InlineData("await t.ConfigureAwait(false);", "CHAOS003")]
    [InlineData("Monitor.Enter(_gate);", "CHAOS004")]
    [InlineData("sem.Wait();", "CHAOS004")]
    [InlineData("var r = t.Result;", "CHAOS005")]
    [InlineData("t.Wait();", "CHAOS005")]
    [InlineData("var r = t.GetAwaiter().GetResult();", "CHAOS005")]
    [InlineData("var now = DateTime.UtcNow;", "CHAOS006")]
    [InlineData("var r = new Random().Next();", "CHAOS006")]
    [InlineData("var r = Random.Shared.Next();", "CHAOS006")]
    [InlineData("var tick = Environment.TickCount64;", "CHAOS006")]
    public async Task Flags_uncontrolled_constructs(string code, string expectedId)
    {
        string[] ids = await AnalyzeAsync(code);
        Assert.Contains(expectedId, ids);
    }

    [Theory]
    [InlineData("await Task.Delay(TimeSpan.FromSeconds(1), TimeProvider.System);")]
    [InlineData("await t.ConfigureAwait(true);")]
    [InlineData("await t;")]
    [InlineData("await Task.Factory.StartNew(() => 1, CancellationToken.None, TaskCreationOptions.None, scheduler);")]
    [InlineData("lock (_gate) { }")]
    [InlineData("var g = Guid.NewGuid();")]
    [InlineData("await sem.WaitAsync();")]
    public async Task Does_not_flag_controlled_or_harmless_constructs(string code)
    {
        Assert.Empty(await AnalyzeAsync(code));
    }
}
