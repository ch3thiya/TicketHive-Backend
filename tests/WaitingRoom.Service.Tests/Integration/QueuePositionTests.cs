using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using WaitingRoom.Service.Db;
using Xunit;

namespace WaitingRoom.Service.Tests.Integration;

[Collection("Postgres")]
public sealed class QueuePositionTests
{
    private readonly PostgresFixture _db;

    public QueuePositionTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task Position_AfterAdvancingServingNumber_DecreasesForAWaitingCustomer()
    {
        // Arrange
        var repository = CreateRepository();
        var showId = Guid.CreateVersion7();
        var onSaleAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await TestData.SeedQueueAsync(_db.ConnectionString, showId, onSaleAt, admitBatch: 10, status: "Open");
        await TestData.SetNextNumberAsync(_db.ConnectionString, showId, nextNumber: 100);

        var entry = await repository.JoinPostSaleAsync(showId, "customer-1", DateTimeOffset.UtcNow);
        var queueBefore = await repository.GetQueueAsync(showId);
        var positionBefore = entry.QueueNumber!.Value - queueBefore!.ServingNumber;

        // Act
        await repository.TryAdvanceServingNumberAsync(showId);
        var queueAfter = await repository.GetQueueAsync(showId);
        var positionAfter = entry.QueueNumber.Value - queueAfter!.ServingNumber;

        // Assert
        Assert.Equal(positionBefore - 10, positionAfter);
    }

    [Fact]
    public async Task Position_QueueStateReadByANewRepositoryInstance_IsConsistentAcrossARestart()
    {
        // Arrange — a fresh instance never held any in-memory state to lose,
        // because none of this is kept in memory (concurrency.md).
        var showId = Guid.CreateVersion7();
        await TestData.SeedQueueAsync(_db.ConnectionString, showId, DateTimeOffset.UtcNow.AddMinutes(-1), admitBatch: 10, status: "Open");
        await TestData.SetNextNumberAsync(_db.ConnectionString, showId, nextNumber: 100);

        var repositoryBeforeRestart = CreateRepository();
        await repositoryBeforeRestart.TryAdvanceServingNumberAsync(showId);

        // Act — simulate a restart with a brand new repository instance
        var repositoryAfterRestart = CreateRepository();
        var queue = await repositoryAfterRestart.GetQueueAsync(showId);

        // Assert
        Assert.Equal(10, queue!.ServingNumber);
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
