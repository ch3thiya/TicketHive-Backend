using System;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;
using WaitingRoom.Service.Tests.Integration;

namespace WaitingRoom.Service.Tests.Api;

[Collection("Postgres")]
public sealed class QueuePositionApiTests : IDisposable
{
    private readonly PostgresFixture _db;
    private readonly WaitingRoomApiFactory _factory;
    private readonly HttpClient _client;

    public QueuePositionApiTests(PostgresFixture db)
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
    public async Task GetPosition_CustomerNotInQueue_SerializesStatusAsAStringNotAnOrdinal()
    {
        // Arrange
        var showId = Guid.CreateVersion7();
        _client.DefaultRequestHeaders.Add(TestAuthHandler.SubHeaderName, "customer-not-in-queue");

        // Act — the real HTTP endpoint, checking the raw wire format rather
        // than a deserialized DTO, since a raw enum ordinal deserializes
        // into an enum-typed property just fine and would hide the defect.
        var response = await _client.GetAsync($"/api/waiting-room/queues/{showId}/entries/me");
        var json = await response.Content.ReadAsStringAsync();

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.Contains("\"status\":\"NotInQueue\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"status\":0", json, StringComparison.OrdinalIgnoreCase);
    }
}
