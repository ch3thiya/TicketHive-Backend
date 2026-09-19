using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using Xunit;
using Inventory.Service.Tests.Api;

namespace Inventory.Service.Tests.Integration;

// ADR-014 test-first: written and confirmed failing (POST /api/inventory/holds
// doesn't exist yet, HoldsController-13) before any hold implementation
// exists, then unskipped in "feat: add hold endpoints" once it does. Real
// Postgres via Testcontainers, real parallel HTTP requests through the
// actual pipeline, no mocks — the thing under test is the database's own
// row-locking (ADR-007), not application code standing in for it.
[Collection("HoldsApi")]
public sealed class HoldsConcurrencyTests
{
    private readonly HoldsApiFixture _fixture;

    public HoldsConcurrencyTests(HoldsApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task CreateHold_500ParallelRequestsFor100Tickets_ExactlyOneHundredHeldAndRestConflict()
    {
        // Arrange — 500 different customers, one ticket each, 100 available.
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedShowAsync(showId, categoryId, capacity: 100, maxPerCustomer: 1000, highDemand: false);

        // Act
        var responses = await Task.WhenAll(Enumerable.Range(0, 500).Select(i =>
            PostHoldAsync(showId, categoryId, quantity: 1, customerSub: $"customer-{i}", idempotencyKey: $"key-{i}")));

        // Assert — the invariant that matters more than anything else in this brief.
        Assert.Equal(100, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(400, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(0, await GetAvailableAsync(showId, categoryId));
        Assert.Equal(100, await GetHeldQuantityAsync(showId, categoryId));
    }

    [Fact]
    public async Task CreateHold_TenParallelRequestsFromOneCustomer_NeverExceedsLimit()
    {
        // Arrange — one customer, ten simultaneous requests for 2 tickets
        // each, against a per-customer limit of 6. Stock is deliberately not
        // the constraint here.
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedShowAsync(showId, categoryId, capacity: 1000, maxPerCustomer: 6, highDemand: false);
        const string customerSub = "quota-customer";

        // Act
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            PostHoldAsync(showId, categoryId, quantity: 2, customerSub: customerSub, idempotencyKey: $"quota-key-{i}")));

        // Assert
        var heldQuantity = await GetQuotaQuantityAsync(showId, customerSub);
        Assert.True(heldQuantity <= 6, $"Held quantity {heldQuantity} exceeded the limit of 6.");
        Assert.Equal(responses.Count(r => r.StatusCode == HttpStatusCode.Created) * 2, heldQuantity);
        Assert.True(responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity) > 0);
    }

    [Fact]
    public async Task CreateHold_SameIdempotencyKeySentTwiceInParallel_ReturnsOneHold()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedShowAsync(showId, categoryId, capacity: 100, maxPerCustomer: 6, highDemand: false);
        const string customerSub = "retry-customer";
        const string idempotencyKey = "same-key";

        // Act — the same key, sent twice, at the same time.
        var responses = await Task.WhenAll(
            PostHoldAsync(showId, categoryId, quantity: 2, customerSub, idempotencyKey),
            PostHoldAsync(showId, categoryId, quantity: 2, customerSub, idempotencyKey));

        // Assert
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var holdIds = await Task.WhenAll(responses.Select(async r =>
        {
            var body = await r.Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("holdId").GetGuid();
        }));
        Assert.Equal(holdIds[0], holdIds[1]);
        Assert.Equal(1, await GetHoldCountAsync(customerSub, idempotencyKey));
        Assert.Equal(98, await GetAvailableAsync(showId, categoryId));
    }

    [Fact]
    public async Task CreateHold_HighDemandShowWithoutAdmissionToken_ReturnsForbiddenAndStockUntouched()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedShowAsync(showId, categoryId, capacity: 100, maxPerCustomer: 6, highDemand: true);

        // Act — no Admission-Token header.
        var response = await PostHoldAsync(showId, categoryId, quantity: 1, customerSub: "queue-jumper", idempotencyKey: "no-token");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(100, await GetAvailableAsync(showId, categoryId));
    }

    private async Task<HttpResponseMessage> PostHoldAsync(Guid showId, Guid categoryId, int quantity, string customerSub, string idempotencyKey)
    {
        var client = _fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/holds")
        {
            Content = JsonContent.Create(new
            {
                showId,
                items = new[] { new { categoryId, quantity } }
            })
        };
        request.Headers.Add(CustomerTestAuthHandler.SubHeaderName, customerSub);
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        return await client.SendAsync(request);
    }

    private async Task SeedShowAsync(Guid showId, Guid categoryId, int capacity, int maxPerCustomer, bool highDemand)
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

    private async Task<int> GetAvailableAsync(Guid showId, Guid categoryId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT available FROM stock WHERE show_id = @ShowId AND category_id = @CategoryId;", connection);
        command.Parameters.AddWithValue("ShowId", showId);
        command.Parameters.AddWithValue("CategoryId", categoryId);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task<int> GetHeldQuantityAsync(Guid showId, Guid categoryId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT COALESCE(SUM(hi.quantity), 0)
            FROM hold_items hi
            JOIN holds h ON h.id = hi.hold_id
            WHERE h.show_id = @ShowId AND hi.category_id = @CategoryId;
            """, connection);
        command.Parameters.AddWithValue("ShowId", showId);
        command.Parameters.AddWithValue("CategoryId", categoryId);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<int> GetQuotaQuantityAsync(Guid showId, string customerSub)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT quantity FROM customer_quotas WHERE show_id = @ShowId AND customer_sub = @CustomerSub;", connection);
        command.Parameters.AddWithValue("ShowId", showId);
        command.Parameters.AddWithValue("CustomerSub", customerSub);
        var result = await command.ExecuteScalarAsync();
        return result is null ? 0 : (int)result;
    }

    private async Task<int> GetHoldCountAsync(string customerSub, string idempotencyKey)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM holds WHERE customer_sub = @CustomerSub AND idempotency_key = @IdempotencyKey;", connection);
        command.Parameters.AddWithValue("CustomerSub", customerSub);
        command.Parameters.AddWithValue("IdempotencyKey", idempotencyKey);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }
}
