using System;
using System.Reflection;
using DbUp;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks;

public static class DatabaseMigrator
{
    public static void Migrate(string connectionString, Assembly assembly, ILogger logger, Func<string, bool>? scriptFilter = null)
    {
        var normalizedConnectionString = PostgresConnectionString.Normalize(connectionString);

        var builder = DeployChanges.To
            .PostgresqlDatabase(normalizedConnectionString);

        var engineBuilder = scriptFilter is null
            ? builder.WithScriptsEmbeddedInAssembly(assembly)
            : builder.WithScriptsEmbeddedInAssembly(assembly, scriptFilter);

        var upgrader = engineBuilder
            .WithTransaction()
            .LogTo(logger)
            .Build();

        var result = upgrader.PerformUpgrade();

        if (!result.Successful)
        {
            throw new InvalidOperationException("Database migration failed.", result.Error);
        }

        foreach (var script in result.Scripts)
        {
            logger.LogInformation("Migration script {Script} applied", script.Name);
        }
    }
}
