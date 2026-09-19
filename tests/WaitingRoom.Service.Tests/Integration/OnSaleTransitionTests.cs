using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using WaitingRoom.Service.Db;
using Xunit;

namespace WaitingRoom.Service.Tests.Integration;

[Collection("Postgres")]
public sealed class OnSaleTransitionTests
{
    private readonly PostgresFixture _db;

    public OnSaleTransitionTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task RunOnSaleTransitionAsync_PreQueueAndPostSaleEntries_RandomizesPreQueueAndPlacesLateJoinersBehind()
    {
        // Arrange
        var repository = CreateRepository();
        var showId = Guid.CreateVersion7();
        var onSaleAt = DateTimeOffset.UtcNow;
        await TestData.SeedQueueAsync(_db.ConnectionString, showId, onSaleAt);

        var preQueueJoinOrder = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            var customerSub = $"pre-queue-customer-{i}";
            preQueueJoinOrder.Add(customerSub);
            await repository.JoinPreQueueAsync(showId, customerSub, onSaleAt.AddMinutes(-1));
        }

        // Act
        await repository.RunOnSaleTransitionAsync(showId);

        var postSaleCustomers = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var customerSub = $"post-sale-customer-{i}";
            postSaleCustomers.Add(customerSub);
            await repository.JoinPostSaleAsync(showId, customerSub, onSaleAt.AddMinutes(1 + i));
        }

        // Assert — the pre-queue's assigned order does not match its join order
        var preQueueEntries = new List<Models.QueueEntry>();
        foreach (var customerSub in preQueueJoinOrder)
        {
            var entry = await repository.GetEntryAsync(showId, customerSub);
            Assert.NotNull(entry);
            Assert.NotNull(entry!.QueueNumber);
            preQueueEntries.Add(entry);
        }

        var assignedOrder = preQueueEntries.OrderBy(e => e.QueueNumber).Select(e => e.CustomerSub).ToList();
        Assert.NotEqual(preQueueJoinOrder, assignedOrder);

        // Assert — every post-sale joiner has a higher number than every pre-queue entry
        var highestPreQueueNumber = preQueueEntries.Max(e => e.QueueNumber!.Value);

        foreach (var customerSub in postSaleCustomers)
        {
            var entry = await repository.GetEntryAsync(showId, customerSub);
            Assert.NotNull(entry);
            Assert.NotNull(entry!.QueueNumber);
            Assert.True(entry!.QueueNumber!.Value > highestPreQueueNumber);
        }
    }

    private QueueRepository CreateRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString
            })
            .Build();

        return new QueueRepository(new DbConnectionFactory(configuration), TimeProvider.System);
    }
}
