using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using WaitingRoom.Service.Db;
using Xunit;

namespace WaitingRoom.Service.Tests.Integration;

[Collection("Postgres")]
public sealed class QueueProvisioningTests
{
    private readonly PostgresFixture _db;

    public QueueProvisioningTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task CreateQueueIfNotExistsAsync_CalledConcurrentlyForTheSameShow_CreatesExactlyOneQueue()
    {
        // Arrange — two joiners racing to provision the same show's queue
        var showId = Guid.CreateVersion7();
        var onSaleAt = DateTimeOffset.UtcNow.AddHours(1);
        var prequeueOpensAt = onSaleAt.AddMinutes(-30);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await gate.Task;
            var repository = CreateRepository();
            return await repository.CreateQueueIfNotExistsAsync(showId, onSaleAt, prequeueOpensAt, 50, 30);
        }).ToArray();

        // Act
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        // Assert — every caller sees the same row
        Assert.Single(results.Select(q => q.OnSaleAt).Distinct());
        Assert.All(results, q => Assert.Equal(showId, q.ShowId));
    }

    [Fact]
    public async Task CreateQueueIfNotExistsAsync_QueueAlreadyProgressed_LeavesItUnchanged()
    {
        // Arrange
        var repository = CreateRepository();
        var showId = Guid.CreateVersion7();
        var onSaleAt = DateTimeOffset.UtcNow.AddHours(1);
        await repository.CreateQueueIfNotExistsAsync(showId, onSaleAt, onSaleAt.AddMinutes(-30), 50, 30);
        await repository.RunOnSaleTransitionAsync(showId);

        // Act — a second "provision" attempt for the same show must not
        // reset a queue that has already moved past PreQueue.
        var result = await repository.CreateQueueIfNotExistsAsync(showId, onSaleAt.AddHours(5), onSaleAt.AddHours(4), 999, 999);

        // Assert — Postgres timestamptz rounds to microseconds, so compare
        // with a small tolerance rather than exact ticks.
        Assert.Equal(onSaleAt, result.OnSaleAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(50, result.AdmitBatch);
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
