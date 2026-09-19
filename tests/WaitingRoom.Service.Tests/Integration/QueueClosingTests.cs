using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using WaitingRoom.Service.Db;
using WaitingRoom.Service.Models;
using Xunit;

namespace WaitingRoom.Service.Tests.Integration;

[Collection("Postgres")]
public sealed class QueueClosingTests
{
    private readonly PostgresFixture _db;

    public QueueClosingTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task CloseQueueAsync_OpenQueue_ClosesItAndRemovesItFromTheActiveList()
    {
        // Arrange
        var repository = CreateRepository();
        var showId = Guid.CreateVersion7();
        await TestData.SeedQueueAsync(_db.ConnectionString, showId, DateTimeOffset.UtcNow.AddMinutes(-1), status: "Open");

        // Act
        await repository.CloseQueueAsync(showId);

        // Assert
        var queue = await repository.GetQueueAsync(showId);
        var activeShowIds = await repository.GetActiveShowIdsAsync();
        Assert.Equal(QueueStatus.Closed, queue!.Status);
        Assert.DoesNotContain(showId, activeShowIds);
    }

    [Fact]
    public async Task CloseQueueAsync_CalledTwice_IsIdempotent()
    {
        // Arrange
        var repository = CreateRepository();
        var showId = Guid.CreateVersion7();
        await TestData.SeedQueueAsync(_db.ConnectionString, showId, DateTimeOffset.UtcNow.AddMinutes(-1), status: "Open");

        // Act
        await repository.CloseQueueAsync(showId);
        await repository.CloseQueueAsync(showId);

        // Assert
        var queue = await repository.GetQueueAsync(showId);
        Assert.Equal(QueueStatus.Closed, queue!.Status);
    }

    [Fact]
    public async Task TryAdvanceServingNumberAsync_ClosedQueue_NeverAdvances()
    {
        // Arrange — a closed (sold out) queue must never advance toward nothing.
        var repository = CreateRepository();
        var showId = Guid.CreateVersion7();
        await TestData.SeedQueueAsync(_db.ConnectionString, showId, DateTimeOffset.UtcNow.AddMinutes(-1), admitBatch: 10, status: "Closed");

        // Act
        var result = await repository.TryAdvanceServingNumberAsync(showId);

        // Assert
        var queue = await repository.GetQueueAsync(showId);
        Assert.Null(result);
        Assert.Equal(0, queue!.ServingNumber);
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
