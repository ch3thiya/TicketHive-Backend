using Architecture.Tests.Support;
using NetArchTest.Rules;
using Xunit;

namespace Architecture.Tests;

public class DomainPurityTests
{
    private static readonly string[] BannedInfrastructureDependencies =
    [
        "Npgsql",
        "Microsoft.AspNetCore",
        "System.Net.Http",
        "DbUp",
    ];

    [Fact]
    public void Models_do_not_depend_on_infrastructure()
    {
        var failures = new List<string>();

        foreach (var assembly in ServiceAssemblies.All)
        {
            var result = Types.InAssembly(assembly)
                .That().ResideInNamespaceEndingWith(".Models")
                .ShouldNot().HaveDependencyOnAny(BannedInfrastructureDependencies)
                .GetResult();

            if (!result.IsSuccessful)
            {
                failures.Add(
                    $"{assembly.GetName().Name}: {string.Join(", ", result.FailingTypeNames!)}");
            }
        }

        Assert.True(failures.Count == 0,
            "Models/ holds domain entities, state machines and business rules and must not " +
            "reference Npgsql, ASP.NET Core, HttpClient or DbUp (ADR-015, ADR-020). Violations:\n" +
            string.Join("\n", failures));
    }
}
