using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Inventory.Service.Db;
using Inventory.Service.Models;
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
        var summary = await repository.ReleaseExpiredHoldsAsync(timeProvider.GetUtcNow(), batchSize: 200);

        // Assert — ReleaseExpiredHoldsAsync sweeps the whole table (by
        // design, ADR-008), so its aggregate counts only ever grow when
        // other tests' expired holds share this pass; assert at least ours
        // was counted, and check our own hold's state precisely.
        Assert.True(summary.HoldsReleased >= 1);
        Assert.True(summary.TicketsReturned >= 5);
        Assert.Equal("Expired", await GetHoldStatusAsync(holdId));
        Assert.Equal(100, await GetAvailableAsync(showId, categoryId));
        Assert.Equal(0, await GetQuotaAsync(showId, customerSub));
    }

    [Fact]
    public async Task ReleaseExpiredHoldsAsync_HoldNotYetExpired_LeavesItUntouched()
    {
        // Arrange — AC6: a hold whose expiry is still in the future.
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        const string customerSub = "not-yet-expired-customer";
        var now = DateTimeOffset.UtcNow;

        await SeedShowAsync(showId, categoryId, capacity: 100, available: 95);
        var holdId = await SeedActiveHoldAsync(showId, categoryId, customerSub, quantity: 5, expiresAt: now.AddMinutes(5));
        await SeedQuotaAsync(showId, customerSub, quantity: 5);

        var repository = CreateRepository();

        // Act
        await repository.ReleaseExpiredHoldsAsync(now, batchSize: 200);

        // Assert — this hold specifically was left alone. (The sweep is
        // system-wide by design, so other tests' expired holds may also be
        // claimed in the same pass — that's not this test's concern.)
        Assert.Equal("Active", await GetHoldStatusAsync(holdId));
        Assert.Equal(95, await GetAvailableAsync(showId, categoryId));
        Assert.Equal(5, await GetQuotaAsync(showId, customerSub));
    }

    [Fact]
    public async Task ReleaseExpiredHoldsAsync_QuotaAtLimitThenExpired_CustomerCanHoldAgain()
    {
        // Arrange — AC1/AC2: a customer holds up to their per-show limit, the
        // hold expires, and after one sweep pass they can hold up to the
        // limit again.
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        const string customerSub = "limit-customer";
        const int maxPerCustomer = 4;
        var now = DateTimeOffset.UtcNow;

        await SeedShowAsync(showId, categoryId, capacity: 100, available: 100);
        var repository = CreateRepository();

        var firstHold = NewHold(showId, categoryId, customerSub, quantity: maxPerCustomer, idempotencyKey: "first-hold", expiresAt: now.AddMinutes(-1));
        var createResult = await repository.CreateAsync(firstHold, maxPerCustomer);
        Assert.Equal(HoldCreationOutcome.Created, createResult.Outcome);

        // Confirm the customer is genuinely at their limit before the sweep.
        var blockedHold = NewHold(showId, categoryId, customerSub, quantity: 1, idempotencyKey: "blocked", expiresAt: now.AddMinutes(10));
        var blockedResult = await repository.CreateAsync(blockedHold, maxPerCustomer);
        Assert.Equal(HoldCreationOutcome.QuotaExceeded, blockedResult.Outcome);

        // Act — the sweep releases the expired hold (among whatever else is
        // due across the shared database at this moment)...
        await repository.ReleaseExpiredHoldsAsync(now, batchSize: 200);
        Assert.Equal("Expired", await GetHoldStatusAsync(firstHold.Id));

        // ...and the customer can hold up to the limit again.
        var secondHold = NewHold(showId, categoryId, customerSub, quantity: maxPerCustomer, idempotencyKey: "second-hold", expiresAt: now.AddMinutes(10));
        var secondResult = await repository.CreateAsync(secondHold, maxPerCustomer);

        // Assert
        Assert.Equal(HoldCreationOutcome.Created, secondResult.Outcome);
        Assert.Equal(maxPerCustomer, await GetQuotaAsync(showId, customerSub));
    }

    [Fact]
    public async Task ReleaseExpiredHoldsAsync_MoreExpiredHoldsThanBatchSize_ClearsOverSeveralPassesWithNoneSkipped()
    {
        // Arrange — five expired holds, a batch size of two.
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        const int holdCount = 5;
        const int batchSize = 2;

        await SeedShowAsync(showId, categoryId, capacity: 100, available: 90);
        var holdIds = new List<Guid>();
        for (int i = 0; i < holdCount; i++)
        {
            var customerSub = $"batch-customer-{i}";
            holdIds.Add(await SeedActiveHoldAsync(showId, categoryId, customerSub, quantity: 2, expiresAt: now.AddMinutes(-1)));
            await SeedQuotaAsync(showId, customerSub, quantity: 2);
        }

        var repository = CreateRepository();

        // Act — one pass never releases more than batchSize, however many
        // expired holds exist system-wide (the sweep is not scoped to one
        // show, ADR-008).
        var firstPass = await repository.ReleaseExpiredHoldsAsync(now, batchSize);
        Assert.True(firstPass.HoldsReleased <= batchSize, $"A single pass released {firstPass.HoldsReleased}, more than the batch size {batchSize}.");

        // Repeated passes eventually clear every one of ours — checked
        // directly by hold id rather than by trusting the pass-by-pass
        // aggregate, since other tests' expired holds may share these same
        // passes without ever exceeding a generous number of attempts.
        for (int pass = 0; pass < holdCount + 5 && !await AllExpiredAsync(holdIds); pass++)
        {
            await repository.ReleaseExpiredHoldsAsync(now, batchSize);
        }

        // Assert — none of our holds were skipped or lost.
        foreach (var holdId in holdIds)
        {
            Assert.Equal("Expired", await GetHoldStatusAsync(holdId));
        }
        Assert.Equal(100, await GetAvailableAsync(showId, categoryId));
    }

    private async Task<bool> AllExpiredAsync(List<Guid> holdIds)
    {
        foreach (var holdId in holdIds)
        {
            if (await GetHoldStatusAsync(holdId) != "Expired")
            {
                return false;
            }
        }

        return true;
    }

    private static Hold NewHold(Guid showId, Guid categoryId, string customerSub, int quantity, string idempotencyKey, DateTimeOffset expiresAt) => new()
    {
        Id = Guid.CreateVersion7(),
        ShowId = showId,
        CustomerSub = customerSub,
        Status = HoldStatus.Active,
        ExpiresAt = expiresAt,
        IdempotencyKey = idempotencyKey,
        CreatedAt = expiresAt.AddMinutes(-10),
        Items = new List<HoldItem> { new() { CategoryId = categoryId, Quantity = quantity } }
    };

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
