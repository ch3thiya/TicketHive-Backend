using Architecture.Tests.Support;
using NetArchTest.Rules;
using Xunit;

namespace Architecture.Tests;

public class ServiceIndependenceTests
{
    [Fact]
    public void No_service_assembly_depends_on_another_service_assembly()
    {
        var failures = new List<string>();

        foreach (var assembly in ServiceAssemblies.All)
        {
            var otherServiceNames = ServiceAssemblies.All
                .Select(other => other.GetName().Name!)
                .Where(name => name != assembly.GetName().Name)
                .ToArray();

            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny(otherServiceNames)
                .GetResult();

            if (!result.IsSuccessful)
            {
                failures.Add(
                    $"{assembly.GetName().Name}: {string.Join(", ", result.FailingTypeNames)}");
            }
        }

        Assert.True(failures.Count == 0,
            "Each service owns its own database and business rules, so no service " +
            "assembly may depend on another service assembly (ADR-020). Violations:\n" +
            string.Join("\n", failures));
    }

    [Fact]
    public void BuildingBlocks_depends_on_no_service_assembly()
    {
        var serviceNames = ServiceAssemblies.All
            .Select(assembly => assembly.GetName().Name!)
            .ToArray();

        var result = Types.InAssembly(ServiceAssemblies.BuildingBlocks)
            .ShouldNot()
            .HaveDependencyOnAny(serviceNames)
            .GetResult();

        var offendingTypes = result.FailingTypeNames ?? [];

        Assert.True(result.IsSuccessful,
            "BuildingBlocks holds shared technical plumbing only and must not depend on " +
            "any service (ADR-015, ADR-020). Offending types: " +
            string.Join(", ", offendingTypes));
    }
}
