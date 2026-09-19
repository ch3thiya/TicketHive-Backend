using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using WaitingRoom.Service.Db;
using Xunit;

namespace WaitingRoom.Service.Tests.Integration;

[Collection("Postgres")]
public sealed class SchedulerAdvancementTests
{
    private readonly PostgresFixture _db;

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
