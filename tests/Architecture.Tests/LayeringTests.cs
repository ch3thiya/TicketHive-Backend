using Architecture.Tests.Support;
using NetArchTest.Rules;
using Xunit;

namespace Architecture.Tests;

public class LayeringTests
{
    [Fact]
    public void Controllers_do_not_depend_on_Npgsql()
    {
        var failures = new List<string>();

        foreach (var assembly in ServiceAssemblies.All)
        {
            var result = Types.InAssembly(assembly)
                .That().ResideInNamespaceEndingWith(".Controllers")
                .ShouldNot().HaveDependencyOn("Npgsql")
                .GetResult();

            if (!result.IsSuccessful)
            {
                failures.Add(
                    $"{assembly.GetName().Name}: {string.Join(", ", result.FailingTypeNames!)}");
            }
        }

        Assert.True(failures.Count == 0,
            "Controllers/ handles HTTP only and must call services rather than the database " +
            "directly, so it must not depend on Npgsql (ADR-015). Violations:\n" +
            string.Join("\n", failures));
    }

    [Fact]
    public void Db_does_not_depend_on_AspNetCore()
    {
        var failures = new List<string>();

        foreach (var assembly in ServiceAssemblies.All)
        {
            var result = Types.InAssembly(assembly)
                .That().ResideInNamespaceEndingWith(".Db")
                .ShouldNot().HaveDependencyOn("Microsoft.AspNetCore")
                .GetResult();

            if (!result.IsSuccessful)
            {
                failures.Add(
                    $"{assembly.GetName().Name}: {string.Join(", ", result.FailingTypeNames!)}");
            }
        }

        Assert.True(failures.Count == 0,
            "Db/ holds repositories and must stay independent of the web framework, so it " +
            "must not depend on Microsoft.AspNetCore (ADR-015). Violations:\n" +
            string.Join("\n", failures));
    }
}
