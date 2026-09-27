using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using WaitingRoom.Service.Db;
using Xunit;

namespace WaitingRoom.Service.Tests.Integration;

[Collection("Postgres")]
public sealed class SchedulerAdvancementTests
{
    private readonly PostgresFixture _db;

    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));

    public SchedulerAdvancementTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task TryAdvanceServingNumberAsync_TwoInstancesAtTheSameTick_AdvancesByExactlyOneBatch()
    {
        // Arrange — an open queue with plenty of assigned numbers ahead of the serving number
        var showId = Guid.CreateVersion7();
        var onSaleAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await TestData.SeedQueueAsync(_db.ConnectionString, showId, onSaleAt, admitBatch: 50, status: "Open");
        await TestData.SetNextNumberAsync(_db.ConnectionString, showId, nextNumber: 500);

        var repositoryA = CreateRepository();
        var repositoryB = CreateRepository();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var taskA = Task.Run(async () =>
        {
            await gate.Task;
            return await repositoryA.TryAdvanceServingNumberAsync(showId);
        });
        var taskB = Task.Run(async () =>
        {
            await gate.Task;
            return await repositoryB.TryAdvanceServingNumberAsync(showId);
        });

        // Act — both instances race for the same tick
        gate.SetResult();
        var results = await Task.WhenAll(taskA, taskB);

        // Assert — only one instance advanced it, and only by one batch
        var servingNumber = await TestData.GetServingNumberAsync(_db.ConnectionString, showId);
        Assert.Equal(50, servingNumber);
        Assert.Single(results, r => r is not null);
    }

    [Fact]
    public async Task TryAdvanceServingNumberAsync_SecondInstanceArrivesAfterTheFirstCommits_DoesNotAdvanceAgain()
    {
        // Arrange — the first instance has already advanced and released its
        // lock; the second reaches the lock cleanly within the same interval.
        var showId = Guid.CreateVersion7();
        await TestData.SeedQueueAsync(_db.ConnectionString, showId, DateTimeOffset.UtcNow.AddMinutes(-1), admitBatch: 50, admitIntervalSeconds: 30, status: "Open");
        await TestData.SetNextNumberAsync(_db.ConnectionString, showId, nextNumber: 500);
        var repositoryA = CreateRepository();
        var repositoryB = CreateRepository();
        await repositoryA.TryAdvanceServingNumberAsync(showId);
        _timeProvider.Advance(TimeSpan.FromSeconds(29));

        // Act
        var result = await repositoryB.TryAdvanceServingNumberAsync(showId);

        // Assert — the interval, not lock timing, holds the rate at one batch
        var servingNumber = await TestData.GetServingNumberAsync(_db.ConnectionString, showId);
        Assert.Null(result);
        Assert.Equal(50, servingNumber);
    }

    [Fact]
    public async Task TryAdvanceServingNumberAsync_OnceTheIntervalHasPassed_AdvancesTheNextBatch()
    {
        // Arrange
        var repository = CreateRepository();
        var showId = Guid.CreateVersion7();
        await TestData.SeedQueueAsync(_db.ConnectionString, showId, DateTimeOffset.UtcNow.AddMinutes(-1), admitBatch: 50, admitIntervalSeconds: 30, status: "Open");
        await TestData.SetNextNumberAsync(_db.ConnectionString, showId, nextNumber: 500);
        await repository.TryAdvanceServingNumberAsync(showId);
        _timeProvider.Advance(TimeSpan.FromSeconds(30));

        // Act
        var result = await repository.TryAdvanceServingNumberAsync(showId);

        // Assert
        Assert.Equal(100, result);
    }

    [Fact]
    public async Task TryAdvanceServingNumberAsync_FewerJoinersThanTheBatch_StopsAtNextNumberInsteadOfOvershooting()
    {
        // Arrange — a queue that emptied after a single joiner (queue_number
        // 1, so next_number is 2); admit_batch is far larger than that.
        var repository = CreateRepository();
        var showId = Guid.CreateVersion7();
        await TestData.SeedQueueAsync(_db.ConnectionString, showId, DateTimeOffset.UtcNow.AddMinutes(-1), admitBatch: 50, admitIntervalSeconds: 30, status: "Open");
        await TestData.SetNextNumberAsync(_db.ConnectionString, showId, nextNumber: 2);

        // Act — the second call is a full interval later, so only the
        // next_number cap can stop it.
        var first = await repository.TryAdvanceServingNumberAsync(showId);
        _timeProvider.Advance(TimeSpan.FromSeconds(30));
        var second = await repository.TryAdvanceServingNumberAsync(showId);

        // Assert — stops at next_number rather than racing ahead to 50, and
        // the next tick finds nothing left to advance toward.
        Assert.Equal(2, first);
        Assert.Null(second);
    }

    private QueueRepository CreateRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString
            })
            .Build();

        return new QueueRepository(new DbConnectionFactory(configuration), _timeProvider);
    }
}
