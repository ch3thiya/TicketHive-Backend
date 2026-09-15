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

// Validation and ownership behaviour that doesn't need real parallelism —
// unlike HoldsConcurrencyTests, sequential real requests against a real
// database are enough here.
[Collection("HoldsApi")]
public sealed class HoldsApiTests
{
    private readonly HoldsApiFixture _fixture;

    public HoldsApiTests(HoldsApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task CreateHold_MissingIdempotencyKey_ReturnsBadRequest()
    {
        var client = _fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/holds")
        {
            Content = JsonContent.Create(new { showId = Guid.NewGuid(), items = new[] { new { categoryId = Guid.NewGuid(), quantity = 1 } } })
        };
        request.Headers.Add(CustomerTestAuthHandler.SubHeaderName, "customer-1");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateHold_EmptyItems_ReturnsBadRequest()
    {
        var showId = Guid.NewGuid();
        var response = await PostHoldRawAsync(showId, items: Array.Empty<object>(), customerSub: "customer-1", idempotencyKey: "key-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CreateHold_NonPositiveQuantity_ReturnsBadRequest(int quantity)
    {
        var showId = Guid.NewGuid();
        var response = await PostHoldRawAsync(
            showId,
            items: new object[] { new { categoryId = Guid.NewGuid(), quantity } },
            customerSub: "customer-1",
            idempotencyKey: "key-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateHold_UnknownShow_ReturnsNotFound()
    {
        var response = await PostHoldRawAsync(
            Guid.NewGuid(),
            items: new object[] { new { categoryId = Guid.NewGuid(), quantity = 1 } },
            customerSub: "customer-1",
            idempotencyKey: "key-1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateHold_UnknownCategory_ReturnsNotFound()
    {
        var showId = Guid.NewGuid();
        await SeedShowAsync(showId, Guid.NewGuid(), capacity: 10, maxPerCustomer: 6);

        var response = await PostHoldRawAsync(
            showId,
            items: new object[] { new { categoryId = Guid.NewGuid(), quantity = 1 } },
            customerSub: "customer-1",
            idempotencyKey: "key-1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetHold_Owner_ReturnsOk()
    {
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedShowAsync(showId, categoryId, capacity: 10, maxPerCustomer: 6);
        var holdId = await CreateHoldAsync(showId, categoryId, customerSub: "owner", idempotencyKey: "owner-key");

        var client = _fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/inventory/holds/{holdId}");
        request.Headers.Add(CustomerTestAuthHandler.SubHeaderName, "owner");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetHold_AnotherCustomer_ReturnsNotFound()
    {
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedShowAsync(showId, categoryId, capacity: 10, maxPerCustomer: 6);
        var holdId = await CreateHoldAsync(showId, categoryId, customerSub: "owner", idempotencyKey: "owner-key-2");

        var client = _fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/inventory/holds/{holdId}");
        request.Headers.Add(CustomerTestAuthHandler.SubHeaderName, "someone-else");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<Guid> CreateHoldAsync(Guid showId, Guid categoryId, string customerSub, string idempotencyKey)
    {
        var response = await PostHoldRawAsync(
            showId,
            items: new object[] { new { categoryId, quantity = 1 } },
            customerSub,
            idempotencyKey);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("holdId").GetGuid();
    }

    private async Task<HttpResponseMessage> PostHoldRawAsync(Guid showId, object[] items, string customerSub, string idempotencyKey)
    {
        var client = _fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/holds")
        {
            Content = JsonContent.Create(new { showId, items })
        };
        request.Headers.Add(CustomerTestAuthHandler.SubHeaderName, customerSub);
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        return await client.SendAsync(request);
    }

    private async Task SeedShowAsync(Guid showId, Guid categoryId, int capacity, int maxPerCustomer, bool highDemand = false)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO show_rules (show_id, organizer_id, max_per_customer, hold_minutes, high_demand)
            VALUES (@ShowId, @OrganizerId, @MaxPerCustomer, 10, @HighDemand);
            """, connection))
        {
            command.Parameters.AddWithValue("ShowId", showId);
            command.Parameters.AddWithValue("OrganizerId", Guid.NewGuid());
            command.Parameters.AddWithValue("MaxPerCustomer", maxPerCustomer);
            command.Parameters.AddWithValue("HighDemand", highDemand);
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO stock (show_id, category_id, capacity, available, unit_price, currency)
            VALUES (@ShowId, @CategoryId, @Capacity, @Capacity, 50.00, 'LKR');
            """, connection))
        {
            command.Parameters.AddWithValue("ShowId", showId);
            command.Parameters.AddWithValue("CategoryId", categoryId);
            command.Parameters.AddWithValue("Capacity", capacity);
            await command.ExecuteNonQueryAsync();
        }
    }
}
