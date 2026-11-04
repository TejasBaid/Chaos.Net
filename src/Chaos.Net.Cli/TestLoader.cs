using System.Reflection;
using System.Runtime.Loader;
using ChaosNet.Xunit;

namespace ChaosNet.Cli;

/// <summary>Loads a test method from an assembly on disk and turns it into a Chaos.Net test body.</summary>
internal static class TestLoader
{
    public static (Func<Task> Body, string FullName, string AssemblyPath) Load(string assemblyPath, string testName)
    {
        string fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Test assembly not found: {fullPath}");
        }

        var resolver = new AssemblyDependencyResolver(fullPath);
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string? path = resolver.ResolveAssemblyToPath(name);
            return path is null ? null : context.LoadFromAssemblyPath(path);
        };

        // Chaos.Net itself is already loaded by the CLI; loading the test into the default context makes the test
        // bind to that same copy, so its Chaos.* calls see the CLI's scheduler.
        Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
        int dot = testName.LastIndexOf('.');
        if (dot <= 0)
        {
            throw new ArgumentException($"Test name '{testName}' must be Namespace.Type.Method.");
        }

        string typeName = testName[..dot];
        string methodName = testName[(dot + 1)..];
        Type type = assembly.GetType(typeName, throwOnError: false)
            ?? throw new ArgumentException($"Type '{typeName}' not found in {Path.GetFileName(fullPath)}.");
        MethodInfo method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance, Type.EmptyTypes)
            ?? throw new ArgumentException($"Parameterless method '{methodName}' not found on {typeName}.");
        return (ChaosTestBody.Create(type, method), testName, fullPath);
    }

    public static IEnumerable<string> List(string assemblyPath)
    {
        string fullPath = Path.GetFullPath(assemblyPath);
        var resolver = new AssemblyDependencyResolver(fullPath);
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string? path = resolver.ResolveAssemblyToPath(name);
            return path is null ? null : context.LoadFromAssemblyPath(path);
        };
        Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = [.. e.Types.OfType<Type>()];
        }

        foreach (Type type in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                bool chaosFact = m.GetCustomAttributes().Any(a => a.GetType().Name == nameof(ChaosFactAttribute));
                bool staticEntry = m.IsStatic && m.GetParameters().Length == 0 && m.ReturnType == typeof(Task) && m.Name.EndsWith("Test", StringComparison.Ordinal);
                if (chaosFact || staticEntry)
                {
                    yield return $"{type.FullName}.{m.Name}{(chaosFact ? "  [ChaosFact]" : string.Empty)}";
                }
            }
        }
    }
}
