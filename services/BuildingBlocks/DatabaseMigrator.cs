using System;
using System.Reflection;
using DbUp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks;

public static class DatabaseMigrator
{
    public static void MigrateIfDevelopment(IHostEnvironment environment, IConfiguration configuration, Action<string> migrate)
    {
        if (!environment.IsDevelopment())
        {
            return;
        }

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is missing from configuration.");

        migrate(connectionString);
    }

    public static void Migrate(string connectionString, Assembly assembly, ILogger logger, Func<string, bool>? scriptFilter = null)
    {
        var normalizedConnectionString = PostgresConnectionString.Normalize(connectionString);

        EnsureDatabase.For.PostgresqlDatabase(normalizedConnectionString);

        var builder = DeployChanges.To
            .PostgresqlDatabase(normalizedConnectionString);

        var engineBuilder = scriptFilter is null
            ? builder.WithScriptsEmbeddedInAssembly(assembly)
            : builder.WithScriptsEmbeddedInAssembly(assembly, scriptFilter);

        var upgrader = engineBuilder
            .JournalToPostgresqlTable("public", "schemaversions")
            .WithTransactionPerScript()
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
