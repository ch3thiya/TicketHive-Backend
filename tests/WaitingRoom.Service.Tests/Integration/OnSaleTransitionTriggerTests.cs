using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using WaitingRoom.Service.Clients;
using WaitingRoom.Service.Db;
using WaitingRoom.Service.Models;
using WaitingRoom.Service.Services;

namespace WaitingRoom.Service.Tests.Integration;

// Reproduces a defect found in manual testing: a queue whose on_sale_at has
// already passed stayed in PreQueue forever, because nothing ever called
// RunOnSaleTransitionAsync, and a customer joining in that state was routed
// into JoinPostSaleAsync anyway and got queue_number 0 (next_number's
// untouched default) instead of the transition ever having run.
[Collection("Postgres")]
public sealed class OnSaleTransitionTriggerTests
{
    private readonly PostgresFixture _db;

    public OnSaleTransitionTriggerTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task JoinAsync_OnSaleTimeAlreadyPassed_RunsTheTransitionBeforeRoutingTheJoin()
    {
        // Arrange — on-sale was 30 minutes ago; one customer has been
        // waiting in the pre-queue since before it opened.
        var showId = Guid.CreateVersion7();
        var onSaleAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        await TestData.SeedQueueAsync(_db.ConnectionString, showId, onSaleAt, status: "PreQueue");

        var repository = CreateRepository();
        await repository.JoinPreQueueAsync(showId, "early-customer", onSaleAt.AddMinutes(-10));

        var service = CreateService(repository);

        // Act — a new customer joins long after on-sale
        var lateEntry = await service.JoinAsync(showId, "late-customer");

        // Assert — the transition ran as part of this join: the queue is
        // Open, the early joiner has a real random-order number, and the
        // late joiner is placed behind it, never at 0.
        var earlyEntry = await repository.GetEntryAsync(showId, "early-customer");
        var queue = await repository.GetQueueAsync(showId);

        Assert.Equal(QueueStatus.Open, queue!.Status);
        Assert.NotNull(earlyEntry!.QueueNumber);
        Assert.Equal(1, earlyEntry.QueueNumber);
        Assert.NotNull(lateEntry!.QueueNumber);
        Assert.True(lateEntry.QueueNumber > earlyEntry.QueueNumber);
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

    private static QueueService CreateService(IQueueRepository repository)
    {
        var queueDefaults = Options.Create(new QueueDefaultsOptions());
        return new QueueService(repository, Mock.Of<ICatalogClient>(), TimeProvider.System, Mock.Of<IAdmissionTokenIssuer>(), queueDefaults);
    }
}
