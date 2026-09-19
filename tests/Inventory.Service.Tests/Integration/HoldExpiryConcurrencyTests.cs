using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Inventory.Service.Db;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Inventory.Service.Tests.Integration;

// ADR-014 test-first: proves the sweeper is safe with any number of
// Inventory instances (ADR-008, AC5). It fails against today's code too —
// not because SKIP LOCKED or the transaction boundary are wrong (they
// aren't), but because the stock UPDATE throws on every pass, so no hold
// ever reaches Expired and nothing is released at all. Unskipped in
// "fix: return stock to the correct column on hold expiry" alongside
// HoldExpiryReleaseTests. Real Postgres via Testcontainers, two real
// repository instances racing the same database — a mock cannot exercise
// row-level locking.
[Collection("Postgres")]
public sealed class HoldExpiryConcurrencyTests
{
    private readonly PostgresFixture _db;

    public HoldExpiryConcurrencyTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task ReleaseExpiredHoldsAsync_TwoInstancesConcurrently_EveryHoldReleasedExactlyOnceAndStockNeverExceedsCapacity()
    {
        const int showCount = 3;
        const int holdsPerShow = 10;
        const int quantityPerHold = 2;
        var now = DateTimeOffset.UtcNow;

        var showIds = new Guid[showCount];
        var categoryIds = new Guid[showCount];
        var customerSubsByShow = new List<string>[showCount];

        for (int s = 0; s < showCount; s++)
        {
            showIds[s] = Guid.NewGuid();
            categoryIds[s] = Guid.NewGuid();
            customerSubsByShow[s] = new List<string>();

            var held = holdsPerShow * quantityPerHold;
            await SeedShowAsync(showIds[s], categoryIds[s], capacity: held, available: 0);

            for (int h = 0; h < holdsPerShow; h++)
            {
                var customerSub = $"customer-{s}-{h}";
                customerSubsByShow[s].Add(customerSub);
                await SeedActiveHoldAsync(showIds[s], categoryIds[s], customerSub, quantityPerHold, now.AddMinutes(-1));
                await SeedQuotaAsync(showIds[s], customerSub, quantityPerHold);
            }
        }

        var repositoryOne = CreateRepository();
        var repositoryTwo = CreateRepository();

        // Act — two "instances" sweep the same database at the same time.
        var results = await Task.WhenAll(
            repositoryOne.ReleaseExpiredHoldsAsync(now),
            repositoryTwo.ReleaseExpiredHoldsAsync(now));

        // Assert — every hold released exactly once, split across the two
        // instances however SKIP LOCKED happened to land, never both.
        Assert.Equal(showCount * holdsPerShow, results[0] + results[1]);

        for (int s = 0; s < showCount; s++)
        {
            var expectedCapacity = holdsPerShow * quantityPerHold;
            var available = await GetAvailableAsync(showIds[s], categoryIds[s]);
            Assert.True(available <= expectedCapacity, $"available {available} exceeded capacity {expectedCapacity} for show {showIds[s]}.");
            Assert.Equal(expectedCapacity, available);

            foreach (var customerSub in customerSubsByShow[s])
            {
                Assert.Equal(0, await GetQuotaAsync(showIds[s], customerSub));
            }
        }
    }

    private HoldRepository CreateRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
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

    private async Task SeedActiveHoldAsync(Guid showId, Guid categoryId, string customerSub, int quantity, DateTimeOffset expiresAt, decimal unitPrice = 50.00m)
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
