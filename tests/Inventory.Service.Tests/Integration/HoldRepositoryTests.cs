using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Inventory.Service.Db;
using Inventory.Service.Models;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Inventory.Service.Tests.Integration;

[Collection("Postgres")]
public sealed class HoldRepositoryTests
{
    private readonly PostgresFixture _db;

    public HoldRepositoryTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task CreateAsync_ValidHold_DecrementsAvailableImmediately()
    {
        // Arrange
        var repository = CreateRepository();
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedShowAsync(showId, categoryId, capacity: 100, maxPerCustomer: 6);

        // Act
        var result = await repository.CreateAsync(NewHold(showId, categoryId, quantity: 3), maxPerCustomer: 6);

        // Assert — AC4: availability reflects the hold immediately.
        Assert.Equal(HoldCreationOutcome.Created, result.Outcome);
        Assert.Equal(97, await GetAvailableAsync(showId, categoryId));
    }

    [Fact]
    public async Task GetByIdAsync_AfterStockPriceChanges_HoldKeepsOriginalPrice()
    {
        // Arrange — AC6: the hold keeps the price captured when it was created.
        var repository = CreateRepository();
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedShowAsync(showId, categoryId, capacity: 100, maxPerCustomer: 6, unitPrice: 50.00m);
        var created = await repository.CreateAsync(NewHold(showId, categoryId, quantity: 2), maxPerCustomer: 6);

        // Act — the organizer changes the price after the hold exists.
        await UpdateUnitPriceAsync(showId, categoryId, 75.00m);
        var reread = await repository.GetByIdAsync(created.Hold!.Id);

        // Assert
        Assert.Equal(50.00m, reread!.Items[0].UnitPrice);
    }

    [Fact]
    public async Task CreateAsync_QuotaAccumulatesAcrossSequentialRequests_RefusesTheOneThatCrossesTheLimit()
    {
        // Arrange
        var repository = CreateRepository();
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedShowAsync(showId, categoryId, capacity: 100, maxPerCustomer: 6);
        const string customerSub = "sequential-customer";

        // Act — 2 + 2 + 2 = 6 (at the limit), then one more request of 2 must refuse.
        var first = await repository.CreateAsync(NewHold(showId, categoryId, quantity: 2, customerSub, "key-1"), maxPerCustomer: 6);
        var second = await repository.CreateAsync(NewHold(showId, categoryId, quantity: 2, customerSub, "key-2"), maxPerCustomer: 6);
        var third = await repository.CreateAsync(NewHold(showId, categoryId, quantity: 2, customerSub, "key-3"), maxPerCustomer: 6);
        var fourth = await repository.CreateAsync(NewHold(showId, categoryId, quantity: 2, customerSub, "key-4"), maxPerCustomer: 6);

        // Assert
        Assert.Equal(HoldCreationOutcome.Created, first.Outcome);
        Assert.Equal(HoldCreationOutcome.Created, second.Outcome);
        Assert.Equal(HoldCreationOutcome.Created, third.Outcome);
        Assert.Equal(HoldCreationOutcome.QuotaExceeded, fourth.Outcome);
        Assert.Equal(6, fourth.Limit);
    }

    [Fact]
    public async Task CreateAsync_SecondCategoryHasInsufficientStock_FirstCategoryStockIsUnchanged()
    {
        // Arrange — a multi-category hold is all-or-nothing (ADR-007).
        var repository = CreateRepository();
        var showId = Guid.NewGuid();
        var plentifulCategoryId = Guid.NewGuid();
        var scarceCategoryId = Guid.NewGuid();

        // UUIDv7 ids are time-ordered, so calling CreateVersion7 first
        // guarantees plentifulCategoryId sorts before scarceCategoryId —
        // the ascending category_id order the transaction processes in.
        await SeedShowAsync(showId, plentifulCategoryId, capacity: 100, maxPerCustomer: 100);
        await SeedCategoryAsync(showId, scarceCategoryId, capacity: 1);

        var hold = new Hold
        {
            Id = Guid.CreateVersion7(),
            ShowId = showId,
            CustomerSub = "multi-category-customer",
            Status = HoldStatus.Active,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            IdempotencyKey = "multi-key",
            CreatedAt = DateTimeOffset.UtcNow,
            Items = new List<HoldItem>
            {
                new() { CategoryId = plentifulCategoryId, Quantity = 5 },
                new() { CategoryId = scarceCategoryId, Quantity = 5 }
            }
        };

        // Act
        var result = await repository.CreateAsync(hold, maxPerCustomer: 100);

        // Assert
        Assert.Equal(HoldCreationOutcome.StockUnavailable, result.Outcome);
        Assert.Equal(scarceCategoryId, result.CategoryId);
        Assert.Equal(100, await GetAvailableAsync(showId, plentifulCategoryId));
        Assert.Equal(1, await GetAvailableAsync(showId, scarceCategoryId));
    }

    [Fact]
    public async Task CreateAsync_UnknownCategory_ReturnsCategoryNotFoundAndWritesNothing()
    {
        // Arrange
        var repository = CreateRepository();
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedShowAsync(showId, categoryId, capacity: 100, maxPerCustomer: 6);
        var unknownCategoryId = Guid.NewGuid();

        // Act
        var result = await repository.CreateAsync(NewHold(showId, unknownCategoryId, quantity: 1), maxPerCustomer: 6);

        // Assert
        Assert.Equal(HoldCreationOutcome.CategoryNotFound, result.Outcome);
        Assert.Equal(unknownCategoryId, result.CategoryId);
        Assert.Equal(100, await GetAvailableAsync(showId, categoryId));
    }

    [Fact]
    public async Task GetByIdAsync_UnknownHold_ReturnsNull()
    {
        var repository = CreateRepository();

        var result = await repository.GetByIdAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    private static Hold NewHold(Guid showId, Guid categoryId, int quantity, string customerSub = "customer", string idempotencyKey = "key") => new()
    {
        Id = Guid.CreateVersion7(),
        ShowId = showId,
        CustomerSub = customerSub,
        Status = HoldStatus.Active,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        IdempotencyKey = idempotencyKey,
        CreatedAt = DateTimeOffset.UtcNow,
        Items = new List<HoldItem> { new() { CategoryId = categoryId, Quantity = quantity } }
    };

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

    private async Task SeedShowAsync(Guid showId, Guid categoryId, int capacity, int maxPerCustomer, decimal unitPrice = 50.00m, bool highDemand = false)
    {
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO show_rules (show_id, organizer_id, max_per_customer, hold_minutes, high_demand)
            VALUES (@ShowId, @OrganizerId, @MaxPerCustomer, 10, @HighDemand);
            """, connection))
        {
            command.Parameters.AddWithValue("ShowId", showId);
            command.Parameters.AddWithValue("OrganizerId", Guid.NewGuid());
            command.Parameters.AddWithValue("MaxPerCustomer", maxPerCustomer);
            command.Parameters.AddWithValue("HighDemand", highDemand);
            await command.ExecuteNonQueryAsync();
        }

        await InsertStockAsync(connection, showId, categoryId, capacity, unitPrice);
    }

    private async Task SeedCategoryAsync(Guid showId, Guid categoryId, int capacity, decimal unitPrice = 50.00m)
    {
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await InsertStockAsync(connection, showId, categoryId, capacity, unitPrice);
    }

    private static async Task InsertStockAsync(NpgsqlConnection connection, Guid showId, Guid categoryId, int capacity, decimal unitPrice)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO stock (show_id, category_id, capacity, available, unit_price, currency)
            VALUES (@ShowId, @CategoryId, @Capacity, @Capacity, @UnitPrice, 'LKR');
            """, connection);
        command.Parameters.AddWithValue("ShowId", showId);
        command.Parameters.AddWithValue("CategoryId", categoryId);
        command.Parameters.AddWithValue("Capacity", capacity);
        command.Parameters.AddWithValue("UnitPrice", unitPrice);
        await command.ExecuteNonQueryAsync();
    }

    private async Task UpdateUnitPriceAsync(Guid showId, Guid categoryId, decimal unitPrice)
    {
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE stock SET unit_price = @UnitPrice WHERE show_id = @ShowId AND category_id = @CategoryId;", connection);
        command.Parameters.AddWithValue("UnitPrice", unitPrice);
        command.Parameters.AddWithValue("ShowId", showId);
        command.Parameters.AddWithValue("CategoryId", categoryId);
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
}
