using System;
using System.Reflection;
using DbUp;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks;

public static class DatabaseMigrator
{
    public static void Migrate(string connectionString, Assembly assembly, ILogger logger)
    {
        var normalizedConnectionString = PostgresConnectionString.Normalize(connectionString);

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(normalizedConnectionString)
            .WithScriptsEmbeddedInAssembly(assembly)
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
