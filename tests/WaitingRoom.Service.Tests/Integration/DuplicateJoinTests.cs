using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using WaitingRoom.Service.Db;
using Xunit;

namespace WaitingRoom.Service.Tests.Integration;

[Collection("Postgres")]
public sealed class DuplicateJoinTests
{
    private readonly PostgresFixture _db;

    public DuplicateJoinTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task JoinPreQueueAsync_CalledTwiceSequentially_ReturnsTheSameEntryUnchanged()
    {
        // Arrange
        var showId = Guid.CreateVersion7();
        var customerSub = "customer-duplicate-sequential";
        var onSaleAt = DateTimeOffset.UtcNow.AddMinutes(30);
        await TestData.SeedQueueAsync(_db.ConnectionString, showId, onSaleAt);
        var repository = CreateRepository();

        // Act
        var first = await repository.JoinPreQueueAsync(showId, customerSub, DateTimeOffset.UtcNow);
        var second = await repository.JoinPreQueueAsync(showId, customerSub, DateTimeOffset.UtcNow.AddSeconds(5));

        // Assert
        Assert.Equal(first.RandomRank, second.RandomRank);
        Assert.Equal(first.QueueNumber, second.QueueNumber);
        Assert.Equal(first.JoinedAt, second.JoinedAt);
    }

    [Fact]
    public async Task JoinPreQueueAsync_CalledConcurrentlyForTheSameCustomer_ReturnsOneEntryForEveryCaller()
    {
        // Arrange
        var showId = Guid.CreateVersion7();
        var customerSub = "customer-duplicate-parallel";
        var onSaleAt = DateTimeOffset.UtcNow.AddMinutes(30);
        await TestData.SeedQueueAsync(_db.ConnectionString, showId, onSaleAt);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            await gate.Task;
            var repository = CreateRepository();
            return await repository.JoinPreQueueAsync(showId, customerSub, DateTimeOffset.UtcNow.AddMilliseconds(i));
        }).ToArray();

        // Act
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        // Assert — every caller sees the same single row, not one each
        Assert.Single(results.Select(r => r.RandomRank).Distinct());
        Assert.Single(results.Select(r => r.JoinedAt).Distinct());
        Assert.All(results, r => Assert.Equal(customerSub, r.CustomerSub));
    }

    private QueueRepository CreateRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString
            })
            .Build();

        return new QueueRepository(new DbConnectionFactory(configuration));
    }
}
