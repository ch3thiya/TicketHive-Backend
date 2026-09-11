using System;
using System.Reflection;
using System.Threading.Tasks;
using BuildingBlocks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace BuildingBlocks.Tests.Integration;

[Collection("Postgres")]
public sealed class DatabaseMigratorTests
{
    private readonly PostgresFixture _db;

    public DatabaseMigratorTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task Migrate_EmptyDatabase_CreatesExpectedTablesAndIndexes()
    {
        // Arrange
        var connectionString = await CreateIsolatedDatabaseAsync();

        // Act
        DatabaseMigrator.Migrate(connectionString, Assembly.GetExecutingAssembly(), NullLogger.Instance, IsValidScript);

        // Assert
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        Assert.True(await TableExistsAsync(connection, "widgets"));
        Assert.True(await IndexExistsAsync(connection, "idx_widgets_name"));
    }

    [Fact]
    public async Task Migrate_RunTwice_IsNoOpAndJournalsScriptOnce()
    {
        // Arrange
        var connectionString = await CreateIsolatedDatabaseAsync();

        // Act
        DatabaseMigrator.Migrate(connectionString, Assembly.GetExecutingAssembly(), NullLogger.Instance, IsValidScript);
        DatabaseMigrator.Migrate(connectionString, Assembly.GetExecutingAssembly(), NullLogger.Instance, IsValidScript);

        // Assert
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM schemaversions", connection);
        var appliedCount = (long)(await command.ExecuteScalarAsync())!;
        Assert.Equal(1, appliedCount);
    }

    [Fact]
    public async Task Migrate_ScriptFails_RollsBackAndThrows()
    {
        // Arrange
        var connectionString = await CreateIsolatedDatabaseAsync();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseMigrator.Migrate(connectionString, Assembly.GetExecutingAssembly(), NullLogger.Instance, IsInvalidScript));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        Assert.False(await TableExistsAsync(connection, "broken_widgets"));
        Assert.False(await TableExistsAsync(connection, "schemaversions"));
    }

    private static bool IsValidScript(string resourceName) => resourceName.Contains(".MigrationFixtures.Valid.");

    private static bool IsInvalidScript(string resourceName) => resourceName.Contains(".MigrationFixtures.Invalid.");

    private async Task<string> CreateIsolatedDatabaseAsync()
    {
        var databaseName = $"migrator_test_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(_db.ConnectionString);

        await using (var adminConnection = new NpgsqlConnection(builder.ConnectionString))
        {
            await adminConnection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", adminConnection);
            await command.ExecuteNonQueryAsync();
        }

        builder.Database = databaseName;
        return builder.ConnectionString;
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string tableName)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @TableName)",
            connection);
        command.Parameters.AddWithValue("TableName", tableName);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> IndexExistsAsync(NpgsqlConnection connection, string indexName)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND indexname = @IndexName)",
            connection);
        command.Parameters.AddWithValue("IndexName", indexName);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
