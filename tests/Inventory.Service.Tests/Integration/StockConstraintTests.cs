using System;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Inventory.Service.Tests.Integration;

[Collection("Postgres")]
public sealed class StockConstraintTests
{
    private readonly PostgresFixture _db;

    public StockConstraintTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task InsertStock_NegativeAvailable_ViolatesCheckConstraint()
    {
        // Arrange
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();

        const string sql = """
            INSERT INTO stock (show_id, category_id, capacity, available, unit_price, currency)
            VALUES (@ShowId, @CategoryId, 10, -1, 10.00, 'USD');
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ShowId", Guid.CreateVersion7());
        command.Parameters.AddWithValue("CategoryId", Guid.CreateVersion7());

        // Act & Assert
        await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
    }
}
