using System.ComponentModel;
using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace ChaosNet.Xunit;

/// <summary>Discovers <see cref="ChaosFactAttribute"/> tests. Used by xUnit; not intended to be called directly.</summary>
public sealed class ChaosFactDiscoverer(IMessageSink diagnosticMessageSink) : IXunitTestCaseDiscoverer
{
    /// <inheritdoc />
    public IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute)
    {
        ArgumentNullException.ThrowIfNull(discoveryOptions);
        yield return new ChaosTestCase(diagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod);
    }
}

/// <summary>A test case whose invocation is driven by <see cref="TestEngine"/>. Used by xUnit; not intended to be used directly.</summary>
public sealed class ChaosTestCase : XunitTestCase
{
    /// <summary>Deserialization constructor.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes")]
    public ChaosTestCase()
    {
    }

    /// <summary>Creates the test case.</summary>
    public ChaosTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay, TestMethodDisplayOptions defaultMethodDisplayOptions, ITestMethod testMethod)
        : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod)
    {
    }

    /// <inheritdoc />
    public override Task<RunSummary> RunAsync(IMessageSink diagnosticMessageSink, IMessageBus messageBus, object[] constructorArguments, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource) =>
        new ChaosTestCaseRunner(this, DisplayName, SkipReason, constructorArguments, TestMethodArguments, messageBus, aggregator, cancellationTokenSource).RunAsync();
}

internal sealed class ChaosTestCaseRunner(IXunitTestCase testCase, string displayName, string skipReason, object[] constructorArguments, object[] testMethodArguments, IMessageBus messageBus, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
    : XunitTestCaseRunner(testCase, displayName, skipReason, constructorArguments, testMethodArguments, messageBus, aggregator, cancellationTokenSource)
{
    protected override Task<RunSummary> RunTestAsync() =>
        new ChaosTestRunner(new XunitTest(TestCase, DisplayName), MessageBus, TestClass, ConstructorArguments, TestMethod, TestMethodArguments, SkipReason, BeforeAfterAttributes, new ExceptionAggregator(Aggregator), CancellationTokenSource).RunAsync();
}

internal sealed class ChaosTestRunner(ITest test, IMessageBus messageBus, Type testClass, object[] constructorArguments, MethodInfo testMethod, object[] testMethodArguments, string skipReason, IReadOnlyList<BeforeAfterTestAttribute> beforeAfterAttributes, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
    : XunitTestRunner(test, messageBus, testClass, constructorArguments, testMethod, testMethodArguments, skipReason, beforeAfterAttributes, aggregator, cancellationTokenSource)
{
    protected override Task<decimal> InvokeTestMethodAsync(ExceptionAggregator aggregator) =>
        new ChaosTestInvoker(Test, MessageBus, TestClass, ConstructorArguments, TestMethod, TestMethodArguments, BeforeAfterAttributes, aggregator, CancellationTokenSource).RunAsync();
}

internal sealed class ChaosTestInvoker(ITest test, IMessageBus messageBus, Type testClass, object[] constructorArguments, MethodInfo testMethod, object[] testMethodArguments, IReadOnlyList<BeforeAfterTestAttribute> beforeAfterAttributes, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
    : XunitTestInvoker(test, messageBus, testClass, constructorArguments, testMethod, testMethodArguments, beforeAfterAttributes, aggregator, cancellationTokenSource)
{
    protected override object CallTestMethod(object testClassInstance)
    {
        ChaosFactAttribute attribute = TestMethod.GetCustomAttribute<ChaosFactAttribute>()
            ?? throw new InvalidOperationException("[ChaosFact] attribute not found.");
        string testName = $"{TestClass.FullName}.{TestMethod.Name}";
        TestConfig config = attribute.ToConfig(testName, TestClass.Assembly.Location);
        Func<Task> body = ChaosTestBody.Create(TestClass, TestMethod, ConstructorArguments, TestMethodArguments);
        return RunAndCheck(body, config, attribute);
    }

    private static async Task RunAndCheck(Func<Task> body, TestConfig config, ChaosFactAttribute attribute)
    {
        TestReport report = await TestEngine.RunAsync(body, config).ConfigureAwait(false);
        ChaosAssert.Check(report, attribute.ExpectBug, attribute.ExpectedBug);
    }
}
