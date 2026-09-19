using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using Xunit;
using Inventory.Service.Tests.Api;

namespace Inventory.Service.Tests.Integration;

// AC4: a hold whose expires_at has passed reads as Expired to both the
// customer and Booking's internal caller even before the sweeper has
// touched it. Seeds the hold's status as 'Active' directly — the sweeper
// never runs in this test's short lifetime — so the only way either
// endpoint can report Expired is by applying Hold.EffectiveStatus at read
// time (HoldService.ToResponse/ToInternalResponse), not by reading the
// stored column.
[Collection("HoldsApi")]
public sealed class HoldExpiryReadTests
{
    private readonly HoldsApiFixture _fixture;

    public HoldExpiryReadTests(HoldsApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetHold_Customer_ExpiredHoldNotYetSwept_ReportsExpired()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        const string customerSub = "expiring-owner";
        var holdId = await SeedExpiredActiveHoldAsync(showId, categoryId, customerSub);

        var client = _fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/inventory/holds/{holdId}");
        request.Headers.Add(CustomerTestAuthHandler.SubHeaderName, customerSub);

        // Act
        var response = await client.SendAsync(request);

        // Assert — 200 with Expired, not the stored Active, and the owner
        // still sees their own hold rather than a 404.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Expired", body.GetProperty("status").GetString());
        Assert.Equal("Active", await GetStoredHoldStatusAsync(holdId));
    }

    [Fact]
    public async Task GetHold_Internal_ExpiredHoldNotYetSwept_ReportsExpired()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        const string customerSub = "expiring-owner-internal";
        var holdId = await SeedExpiredActiveHoldAsync(showId, categoryId, customerSub);

        var client = _fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/inventory/holds/{holdId}");
        request.Headers.Add(InternalTestAuthHandler.SubHeaderName, "booking-service");
        request.Headers.Add(InternalTestAuthHandler.ScopeHeaderName, "inventory:write");
        request.Headers.Add(InternalTestAuthHandler.AutHeaderName, "APPLICATION");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Expired", body.GetProperty("status").GetString());
        Assert.Equal(customerSub, body.GetProperty("customerSub").GetString());
        Assert.Equal("Active", await GetStoredHoldStatusAsync(holdId));
    }

    [Fact]
    public async Task GetHold_Internal_UnknownHold_ReturnsNotFound()
    {
        var client = _fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/inventory/holds/{Guid.NewGuid()}");
        request.Headers.Add(InternalTestAuthHandler.SubHeaderName, "booking-service");
        request.Headers.Add(InternalTestAuthHandler.ScopeHeaderName, "inventory:write");
        request.Headers.Add(InternalTestAuthHandler.AutHeaderName, "APPLICATION");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<Guid> SeedExpiredActiveHoldAsync(Guid showId, Guid categoryId, string customerSub)
    {
        var holdId = Guid.CreateVersion7();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO stock (show_id, category_id, capacity, available, unit_price, currency)
            VALUES (@ShowId, @CategoryId, 10, 9, 50.00, 'LKR');
            """, connection))
        {
            command.Parameters.AddWithValue("ShowId", showId);
            command.Parameters.AddWithValue("CategoryId", categoryId);
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO holds (id, show_id, customer_sub, status, expires_at, idempotency_key, created_at)
            VALUES (@Id, @ShowId, @CustomerSub, 'Active', @ExpiresAt, @IdempotencyKey, @CreatedAt);
            """, connection))
        {
            command.Parameters.AddWithValue("Id", holdId);
            command.Parameters.AddWithValue("ShowId", showId);
            command.Parameters.AddWithValue("CustomerSub", customerSub);
            command.Parameters.AddWithValue("ExpiresAt", expiresAt);
            command.Parameters.AddWithValue("IdempotencyKey", $"seed-{holdId}");
            command.Parameters.AddWithValue("CreatedAt", expiresAt.AddMinutes(-10));
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO hold_items (hold_id, category_id, quantity, unit_price)
            VALUES (@HoldId, @CategoryId, 1, 50.00);
            """, connection))
        {
            command.Parameters.AddWithValue("HoldId", holdId);
            command.Parameters.AddWithValue("CategoryId", categoryId);
            await command.ExecuteNonQueryAsync();
        }

        return holdId;
    }

    private async Task<string> GetStoredHoldStatusAsync(Guid holdId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT status FROM holds WHERE id = @Id;", connection);
        command.Parameters.AddWithValue("Id", holdId);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
