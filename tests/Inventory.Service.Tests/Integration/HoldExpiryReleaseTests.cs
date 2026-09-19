using System;
using System.Threading.Tasks;
using Inventory.Service.Db;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;

namespace Inventory.Service.Tests.Integration;

// ADR-014 test-first: this is the regression test for the
// available_quantity/available column bug (BRIEF #17). It fails against
// today's ReleaseExpiredHoldsAsync — the stock UPDATE throws 42703, the
// transaction rolls back, and nothing changes — and is unskipped in
// "fix: return stock to the correct column on hold expiry" once the column
// and show_id scoping are corrected. Real Postgres via Testcontainers; the
// defect is in SQL, so a mocked repository would hide it exactly as it did
// for a whole sprint.
[Collection("Postgres")]
public sealed class HoldExpiryReleaseTests
{
    private readonly PostgresFixture _db;

    public HoldExpiryReleaseTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task ReleaseExpiredHoldsAsync_HoldPastExpiry_RestoresStockAndQuotaAndMarksExpired()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        const string customerSub = "expiring-customer";
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));

        await SeedShowAsync(showId, categoryId, capacity: 100, available: 95);
        var holdId = await SeedActiveHoldAsync(
            showId, categoryId, customerSub, quantity: 5,
            expiresAt: timeProvider.GetUtcNow().AddMinutes(-1));
        await SeedQuotaAsync(showId, customerSub, quantity: 5);

        var repository = CreateRepository();

        // Act — advance past the hold's expiry and run one sweep pass.
        timeProvider.Advance(TimeSpan.FromMinutes(2));
        var releasedCount = await repository.ReleaseExpiredHoldsAsync(timeProvider.GetUtcNow());

        // Assert
        Assert.Equal(1, releasedCount);
        Assert.Equal("Expired", await GetHoldStatusAsync(holdId));
        Assert.Equal(100, await GetAvailableAsync(showId, categoryId));
        Assert.Equal(0, await GetQuotaAsync(showId, customerSub));
    }

    private HoldRepository CreateRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString
            })
            .Build();

        return new HoldRepository(new DbConnectionFactory(configuration), new GeneralAdmissionAllocationStrategy());
    }

    private async Task SeedShowAsync(Guid showId, Guid categoryId, int capacity, int available, decimal unitPrice = 50.00m)
    {
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO stock (show_id, category_id, capacity, available, unit_price, currency)
            VALUES (@ShowId, @CategoryId, @Capacity, @Available, @UnitPrice, 'LKR');
            """, connection);
        command.Parameters.AddWithValue("ShowId", showId);
        command.Parameters.AddWithValue("CategoryId", categoryId);
        command.Parameters.AddWithValue("Capacity", capacity);
        command.Parameters.AddWithValue("Available", available);
        command.Parameters.AddWithValue("UnitPrice", unitPrice);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Guid> SeedActiveHoldAsync(Guid showId, Guid categoryId, string customerSub, int quantity, DateTimeOffset expiresAt, decimal unitPrice = 50.00m)
    {
        var holdId = Guid.CreateVersion7();

        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO holds (id, show_id, customer_sub, status, expires_at, idempotency_key, created_at)
            VALUES (@Id, @ShowId, @CustomerSub, 'Active', @ExpiresAt, @IdempotencyKey, @CreatedAt);
            """, connection))
        {
            command.Parameters.AddWithValue("Id", holdId);
            command.Parameters.AddWithValue("ShowId", showId);
            command.Parameters.AddWithValue("CustomerSub", customerSub);
            command.Parameters.AddWithValue("ExpiresAt", expiresAt);
            command.Parameters.AddWithValue("IdempotencyKey", $"seed-{holdId}");
            command.Parameters.AddWithValue("CreatedAt", expiresAt.AddMinutes(-10));
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO hold_items (hold_id, category_id, quantity, unit_price)
            VALUES (@HoldId, @CategoryId, @Quantity, @UnitPrice);
            """, connection))
        {
            command.Parameters.AddWithValue("HoldId", holdId);
            command.Parameters.AddWithValue("CategoryId", categoryId);
            command.Parameters.AddWithValue("Quantity", quantity);
            command.Parameters.AddWithValue("UnitPrice", unitPrice);
            await command.ExecuteNonQueryAsync();
        }

        return holdId;
    }

    private async Task SeedQuotaAsync(Guid showId, string customerSub, int quantity)
    {
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO customer_quotas (show_id, customer_sub, quantity)
            VALUES (@ShowId, @CustomerSub, @Quantity);
            """, connection);
        command.Parameters.AddWithValue("ShowId", showId);
        command.Parameters.AddWithValue("CustomerSub", customerSub);
        command.Parameters.AddWithValue("Quantity", quantity);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> GetHoldStatusAsync(Guid holdId)
    {
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT status FROM holds WHERE id = @Id;", connection);
        command.Parameters.AddWithValue("Id", holdId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<int> GetAvailableAsync(Guid showId, Guid categoryId)
    {
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT available FROM stock WHERE show_id = @ShowId AND category_id = @CategoryId;", connection);
        command.Parameters.AddWithValue("ShowId", showId);
        command.Parameters.AddWithValue("CategoryId", categoryId);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task<int> GetQuotaAsync(Guid showId, string customerSub)
    {
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT quantity FROM customer_quotas WHERE show_id = @ShowId AND customer_sub = @CustomerSub;", connection);
        command.Parameters.AddWithValue("ShowId", showId);
        command.Parameters.AddWithValue("CustomerSub", customerSub);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
