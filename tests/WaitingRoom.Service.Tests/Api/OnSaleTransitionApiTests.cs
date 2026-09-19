using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Npgsql;
using Xunit;
using WaitingRoom.Service.Services;
using WaitingRoom.Service.Tests.Integration;

namespace WaitingRoom.Service.Tests.Api;

// Reproduces the manual-testing report that defect 3 still occurs against
// the real running service on both the join path and the scheduler path,
// even though the hand-built QueueService integration test passes. This
// test goes through the actual HTTP endpoint, the actual controller, and
// the actual DI-wired QueueService and QueueRepository — nothing hand
// constructed — against a queue row seeded directly in Postgres, exactly
// the shape reported (an existing queue whose on_sale_at is already past).
[Collection("Postgres")]
public sealed class OnSaleTransitionApiTests : IDisposable
{
    private readonly PostgresFixture _db;
    private readonly WaitingRoomApiFactory _factory;
    private readonly HttpClient _client;

    public OnSaleTransitionApiTests(PostgresFixture db)
    {
        _db = db;
        _factory = new WaitingRoomApiFactory(db.ConnectionString);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Join_ExistingQueueWithOnSaleAtAlreadyPast_TransitionsAndNeverAssignsQueueNumberZero()
    {
        // Arrange — seeded directly, the same way it would already exist in
        // the database before this join attempt (never provisioned via
        // Catalog for this test, so no cache is involved on this path).
        var showId = Guid.CreateVersion7();
        var onSaleAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        await TestData.SeedQueueAsync(_db.ConnectionString, showId, onSaleAt, status: "PreQueue");

        _client.DefaultRequestHeaders.Add(TestAuthHandler.SubHeaderName, "customer-real-service");

        // Act — the real HTTP endpoint, not a hand-built QueueService.
        var response = await _client.PostAsync($"/api/waiting-room/queues/{showId}/entries", content: null);

        // Assert
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<QueueEntryResponse>();

        Assert.NotNull(body!.QueueNumber);
        Assert.NotEqual(0, body.QueueNumber);

        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select status from queues where show_id = @ShowId";
        command.Parameters.AddWithValue("ShowId", showId);
        var status = (string)(await command.ExecuteScalarAsync())!;

        Assert.Equal("Open", status);
    }

    [Fact]
    public async Task Scheduler_QueueWithOnSaleAtAlreadyPast_TransitionsWithoutAnyJoinHappening()
    {
        // Arrange — nobody joins; only the real, DI-registered
        // QueueAdmissionScheduler (a real hosted service in this factory,
        // ticking every second) can move this queue out of PreQueue.
        var showId = Guid.CreateVersion7();
        var onSaleAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        await TestData.SeedQueueAsync(_db.ConnectionString, showId, onSaleAt, status: "PreQueue");

        // Act — poll for up to 10 seconds for the scheduler's own tick to
        // act (creating the client in the constructor already started the
        // host, and with it every registered hosted service).
        string? status = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await using var connection = new NpgsqlConnection(_db.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "select status from queues where show_id = @ShowId";
            command.Parameters.AddWithValue("ShowId", showId);
            status = (string)(await command.ExecuteScalarAsync())!;

            if (status == "Open")
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        // Assert
        Assert.Equal("Open", status);
    }
}
