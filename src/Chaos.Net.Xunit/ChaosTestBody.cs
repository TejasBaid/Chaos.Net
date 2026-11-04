using System.Reflection;

namespace ChaosNet.Xunit;

/// <summary>Turns a test method into a Chaos.Net test body that creates a fresh test-class instance per iteration.</summary>
public static class ChaosTestBody
{
    /// <summary>Creates a body that invokes <paramref name="method"/> on a fresh instance of <paramref name="testClass"/>.</summary>
    /// <param name="testClass">The class declaring the method.</param>
    /// <param name="method">A method returning <see cref="void"/>, <see cref="Task"/> or <see cref="ValueTask"/>.</param>
    /// <param name="constructorArguments">Constructor arguments (for example xUnit fixtures).</param>
    /// <param name="methodArguments">Method arguments.</param>
    public static Func<Task> Create(Type testClass, MethodInfo method, object?[]? constructorArguments = null, object?[]? methodArguments = null)
    {
        ArgumentNullException.ThrowIfNull(testClass);
        ArgumentNullException.ThrowIfNull(method);
        return async () =>
        {
            object? instance = method.IsStatic ? null : Activator.CreateInstance(testClass, constructorArguments ?? []);
            try
            {
                object? result;
                try
                {
                    result = method.Invoke(instance, methodArguments ?? []);
                }
                catch (TargetInvocationException e) when (e.InnerException is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e.InnerException);
                    throw;
                }

                switch (result)
                {
                    case Task t:
                        await t;
                        break;
                    case ValueTask vt:
                        await vt;
                        break;
                }
            }
            finally
            {
                switch (instance)
                {
                    case IAsyncDisposable ad:
                        await ad.DisposeAsync();
                        break;
                    case IDisposable d:
                        d.Dispose();
                        break;
                }
            }
        };
    }
}
