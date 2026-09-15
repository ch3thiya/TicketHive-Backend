using System;
using System.Threading.Tasks;
using BuildingBlocks;
using Inventory.Service.Db;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Inventory.Service.Tests.Integration;

public sealed class MigrationTests
{
    // Uses its own throwaway container per test (rather than the shared
    // PostgresFixture) so each test starts from a genuinely empty database
    // and can assert on the migration itself, not just its result.
    [Fact]
    public async Task Migrate_EmptyDatabase_CreatesShowRulesAndStockTables()
    {
        // Arrange
        await using var container = new PostgreSqlBuilder("postgres:15-alpine").Build();
        await container.StartAsync();

        // Act
        DatabaseMigrator.Migrate(container.GetConnectionString(), typeof(DbConnectionFactory).Assembly, NullLogger.Instance);

        // Assert
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();

        Assert.True(await TableExistsAsync(connection, "show_rules"));
        Assert.True(await TableExistsAsync(connection, "stock"));
    }

    [Fact]
    public async Task Migrate_AppliedTwice_IsNoOp()
    {
        // Arrange
        await using var container = new PostgreSqlBuilder("postgres:15-alpine").Build();
        await container.StartAsync();
        DatabaseMigrator.Migrate(container.GetConnectionString(), typeof(DbConnectionFactory).Assembly, NullLogger.Instance);

        // Act & Assert — a second run must not throw or duplicate scripts.
        var exception = Record.Exception(() =>
            DatabaseMigrator.Migrate(container.GetConnectionString(), typeof(DbConnectionFactory).Assembly, NullLogger.Instance));
        Assert.Null(exception);
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string tableName)
    {
        const string sql = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @TableName);";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("TableName", tableName);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
