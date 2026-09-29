using System.Reflection;

namespace Architecture.Tests.Support;

// Discovers service assemblies by the "<Name>.Service" naming convention, not a hard-coded list.
internal static class ServiceAssemblies
{
    public static IReadOnlyList<Assembly> All { get; }

    public static Assembly BuildingBlocks { get; }

    public static Assembly Gateway { get; }

    static ServiceAssemblies()
    {
        var baseDirectory = AppContext.BaseDirectory;

        All = Directory.EnumerateFiles(baseDirectory, "*.Service.dll")
            .Select(Assembly.LoadFrom)
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal)
            .ToList();

        BuildingBlocks = Assembly.LoadFrom(Path.Combine(baseDirectory, "BuildingBlocks.dll"));
        Gateway = Assembly.LoadFrom(Path.Combine(baseDirectory, "Gateway.dll"));
    }
}
