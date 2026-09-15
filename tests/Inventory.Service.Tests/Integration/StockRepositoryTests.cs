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
public sealed class StockRepositoryTests
{
    private readonly PostgresFixture _db;

    public StockRepositoryTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task InitializeAsync_NewShow_CreatesStockAtFullCapacityAndShowRules()
    {
        // Arrange
        var repository = CreateRepository();
        var showId = Guid.CreateVersion7();
        var organizerId = Guid.CreateVersion7();
        var categoryOne = Guid.CreateVersion7();
        var categoryTwo = Guid.CreateVersion7();
        var rules = new ShowRules { ShowId = showId, OrganizerId = organizerId, MaxPerCustomer = 6, HoldMinutes = 10, HighDemand = false };
        var categories = new List<StockItem>
        {
            new() { ShowId = showId, CategoryId = categoryOne, Capacity = 100, UnitPrice = 25.00m, Currency = "LKR", AllocationMode = "GA" },
            new() { ShowId = showId, CategoryId = categoryTwo, Capacity = 50, UnitPrice = 75.00m, Currency = "LKR", AllocationMode = "GA" }
        };

        // Act
        var result = await repository.InitializeAsync(rules, categories);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, item => Assert.Equal(item.Capacity, item.Available));
        Assert.Equal(organizerId, await GetShowRulesOrganizerIdAsync(showId));
    }

    [Fact]
    public async Task InitializeAsync_CalledTwice_SecondCallLeavesStockUnchanged()
    {
        // Arrange
        var repository = CreateRepository();
        var showId = Guid.CreateVersion7();
        var categoryId = Guid.CreateVersion7();
        var rules = new ShowRules { ShowId = showId, OrganizerId = Guid.CreateVersion7(), MaxPerCustomer = 6, HoldMinutes = 10, HighDemand = false };
        var categories = new List<StockItem>
        {
            new() { ShowId = showId, CategoryId = categoryId, Capacity = 100, UnitPrice = 25.00m, Currency = "LKR", AllocationMode = "GA" }
        };
        var first = await repository.InitializeAsync(rules, categories);

        // Act
        var second = await repository.InitializeAsync(rules, categories);

        // Assert
        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first[0].Capacity, second[0].Capacity);
        Assert.Equal(first[0].Available, second[0].Available);
    }

    [Fact]
    public async Task InitializeAsync_AfterAvailableDecremented_RepeatCallDoesNotResetAvailable()
    {
        // Arrange — protects S2-03's holds from being wiped by a republish.
        var repository = CreateRepository();
        var showId = Guid.CreateVersion7();
        var categoryId = Guid.CreateVersion7();
        var rules = new ShowRules { ShowId = showId, OrganizerId = Guid.CreateVersion7(), MaxPerCustomer = 6, HoldMinutes = 10, HighDemand = false };
        var categories = new List<StockItem>
        {
            new() { ShowId = showId, CategoryId = categoryId, Capacity = 100, UnitPrice = 25.00m, Currency = "LKR", AllocationMode = "GA" }
        };
        await repository.InitializeAsync(rules, categories);
        await DecrementAvailableAsync(showId, categoryId, by: 30);

        // Act — simulates Catalog retrying publish after initialize already ran once.
        var result = await repository.InitializeAsync(rules, categories);

        // Assert
        Assert.Equal(70, result[0].Available);
        Assert.Equal(100, result[0].Capacity);
    }

    [Fact]
    public async Task GetByShowIdAsync_UnknownShow_ReturnsEmptyList()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var result = await repository.GetByShowIdAsync(Guid.CreateVersion7());

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetByShowIdAsync_AvailableChangedBetweenCalls_ReflectsCurrentState()
    {
        // Arrange
        var repository = CreateRepository();
        var showId = Guid.CreateVersion7();
        var categoryId = Guid.CreateVersion7();
        var rules = new ShowRules { ShowId = showId, OrganizerId = Guid.CreateVersion7(), MaxPerCustomer = 6, HoldMinutes = 10, HighDemand = false };
        var categories = new List<StockItem>
        {
            new() { ShowId = showId, CategoryId = categoryId, Capacity = 100, UnitPrice = 25.00m, Currency = "LKR", AllocationMode = "GA" }
        };
        await repository.InitializeAsync(rules, categories);

        // Act
        var firstRead = await repository.GetByShowIdAsync(showId);
        await DecrementAvailableAsync(showId, categoryId, by: 40);
        var secondRead = await repository.GetByShowIdAsync(showId);

        // Assert
        Assert.Equal(100, firstRead[0].Available);
        Assert.Equal(60, secondRead[0].Available);
    }

    private StockRepository CreateRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString
            })
            .Build();

        return new StockRepository(new DbConnectionFactory(configuration));
    }

    private async Task<Guid> GetShowRulesOrganizerIdAsync(Guid showId)
    {
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT organizer_id FROM show_rules WHERE show_id = @ShowId;", connection);
        command.Parameters.AddWithValue("ShowId", showId);

        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private async Task DecrementAvailableAsync(Guid showId, Guid categoryId, int by)
    {
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "UPDATE stock SET available = available - @By WHERE show_id = @ShowId AND category_id = @CategoryId;", connection);
        command.Parameters.AddWithValue("By", by);
        command.Parameters.AddWithValue("ShowId", showId);
        command.Parameters.AddWithValue("CategoryId", categoryId);

        await command.ExecuteNonQueryAsync();
    }
}
