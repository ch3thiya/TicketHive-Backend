using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using Xunit;
using Inventory.Service.Models;
using Inventory.Service.Tests.Api;

namespace Inventory.Service.Tests.Integration;

// Real PostgreSQL and the real HTTP pipeline; only Catalog is replaced by a fake whose answer
// per show can be flipped between requests, which is exactly what a suspension looks like to
// Inventory (it never caches the answer).
[Collection("HoldsApi")]
public sealed class OrganizerSuspensionHoldsTests
{
    private readonly HoldsApiFixture _fixture;
    private readonly string _run = Guid.NewGuid().ToString("N");

    public OrganizerSuspensionHoldsTests(HoldsApiFixture fixture) => _fixture = fixture;

    private FakeSalesEligibilityClient Catalog => _fixture.Factory.SalesEligibility;

    [Fact]
    public async Task CreateHold_SuspendedOrganizer_Returns409AndLeavesStockQuotaAndHoldsUntouched()
    {
        // Arrange
        var (showId, categoryId) = await SeedAsync(capacity: 10);
        Catalog.Set(showId, SalesEligibilityStatus.OrganizerSuspended);

        // Act
        var response = await PostHoldAsync(showId, categoryId, 2, "customer-1", "key-1");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(10, await ScalarAsync("SELECT available FROM stock WHERE show_id = @s", showId));
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM holds WHERE show_id = @s", showId));
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM customer_quotas WHERE show_id = @s", showId));
    }

    [Fact]
    public async Task CreateHold_SuspendedOrganizer_ResponseDoesNotRevealWhy()
    {
        // Arrange
        var (showId, categoryId) = await SeedAsync(capacity: 10);
        Catalog.Set(showId, SalesEligibilityStatus.OrganizerSuspended);

        // Act
        var text = await (await PostHoldAsync(showId, categoryId, 1, "customer-1", "key-1")).Content.ReadAsStringAsync();

        // Assert
        Assert.DoesNotContain("suspended", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("organizer", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateHold_CatalogCannotVerify_Returns503WithRetryAfterAndNoHold()
    {
        // Arrange
        var (showId, categoryId) = await SeedAsync(capacity: 10);
        Catalog.Set(showId, SalesEligibilityStatus.Unavailable);

        // Act
        var response = await PostHoldAsync(showId, categoryId, 1, "customer-1", "key-1");

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.Contains("Retry-After"));
        Assert.Equal(10, await ScalarAsync("SELECT available FROM stock WHERE show_id = @s", showId));
    }

    [Fact]
    public async Task CreateHold_ShowNotOnSale_Returns409()
    {
        // Arrange
        var (showId, categoryId) = await SeedAsync(capacity: 10);
        Catalog.Set(showId, SalesEligibilityStatus.NotOnSale);

        // Act
        var response = await PostHoldAsync(showId, categoryId, 1, "customer-1", "key-1");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateHold_SuspendThenReinstate_TakesEffectOnTheNextRequestWithoutRestart()
    {
        // Arrange
        var (showId, categoryId) = await SeedAsync(capacity: 10);

        // Act
        var before = await PostHoldAsync(showId, categoryId, 1, "customer-1", "key-a");
        Catalog.Set(showId, SalesEligibilityStatus.OrganizerSuspended);
        var during = await PostHoldAsync(showId, categoryId, 1, "customer-2", "key-b");
        Catalog.Set(showId, SalesEligibilityStatus.Eligible);
        var after = await PostHoldAsync(showId, categoryId, 1, "customer-3", "key-c");

        // Assert
        Assert.Equal(HttpStatusCode.Created, before.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, during.StatusCode);
        Assert.Equal(HttpStatusCode.Created, after.StatusCode);
        Assert.Equal(8, await ScalarAsync("SELECT available FROM stock WHERE show_id = @s", showId));
    }

    [Fact]
    public async Task CreateHold_RetryOfAHoldCreatedBeforeSuspension_ReturnsTheSameHold()
    {
        // Arrange
        var (showId, categoryId) = await SeedAsync(capacity: 10);
        var first = await PostHoldAsync(showId, categoryId, 2, "customer-1", "same-key");
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("holdId").GetGuid();
        Catalog.Set(showId, SalesEligibilityStatus.OrganizerSuspended);

        // Act
        var retry = await PostHoldAsync(showId, categoryId, 2, "customer-1", "same-key");

        // Assert
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(firstId, (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("holdId").GetGuid());
        Assert.Equal(8, await ScalarAsync("SELECT available FROM stock WHERE show_id = @s", showId));
    }

    [Fact]
    public async Task ExistingHold_AfterSuspension_CanStillBeFrozenConvertedAndKeepsStock()
    {
        // Arrange: a customer is already in checkout when the organizer is suspended.
        var (showId, categoryId) = await SeedAsync(capacity: 10);
        var holdResponse = await PostHoldAsync(showId, categoryId, 2, "customer-1", "key-1");
        var holdId = (await holdResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("holdId").GetGuid();
        Catalog.Set(showId, SalesEligibilityStatus.OrganizerSuspended);

        // Act
        var freeze = await InternalPatchAsync($"/internal/inventory/holds/{holdId}/freeze");
        var convert = await InternalPatchAsync($"/internal/inventory/holds/{holdId}/convert");

        // Assert: provisional rule - in-flight checkouts finish; nothing is released or refunded.
        Assert.Equal(HttpStatusCode.OK, freeze.StatusCode);
        Assert.Equal(HttpStatusCode.OK, convert.StatusCode);
        Assert.Equal(8, await ScalarAsync("SELECT available FROM stock WHERE show_id = @s", showId));
        Assert.Equal(2, await ScalarAsync("SELECT sold FROM stock WHERE show_id = @s", showId));
    }

    [Fact]
    public async Task SoldStock_WhenOrganizerIsSuspendedAndReinstated_IsNeverChanged()
    {
        // Arrange
        var (showId, categoryId) = await SeedAsync(capacity: 10);
        var holdId = (await (await PostHoldAsync(showId, categoryId, 3, "customer-1", "key-1")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("holdId").GetGuid();
        await InternalPatchAsync($"/internal/inventory/holds/{holdId}/convert");

        // Act
        Catalog.Set(showId, SalesEligibilityStatus.OrganizerSuspended);
        await PostHoldAsync(showId, categoryId, 1, "customer-2", "key-2");
        Catalog.Set(showId, SalesEligibilityStatus.Eligible);

        // Assert
        Assert.Equal(3, await ScalarAsync("SELECT sold FROM stock WHERE show_id = @s", showId));
        Assert.Equal(7, await ScalarAsync("SELECT available FROM stock WHERE show_id = @s", showId));
    }

    [Fact]
    public async Task CreateHold_CancelledShowAfterReinstatement_StaysClosed()
    {
        // Arrange: the show was cancelled (tombstone) and the organizer is reinstated afterwards.
        var (showId, categoryId) = await SeedAsync(capacity: 10);
        await ExecuteAsync("SELECT close_cancelled_show(@s)", showId);
        Catalog.Set(showId, SalesEligibilityStatus.Eligible);

        // Act
        var response = await PostHoldAsync(showId, categoryId, 1, "customer-1", "key-1");

        // Assert: Inventory's cancellation tombstone is independent of, and not undone by, reinstatement.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(10, await ScalarAsync("SELECT available FROM stock WHERE show_id = @s", showId));
    }

    [Fact]
    public async Task CreateHold_SuspensionDoesNotWriteACancellationTombstone()
    {
        // Arrange
        var (showId, categoryId) = await SeedAsync(capacity: 10);
        Catalog.Set(showId, SalesEligibilityStatus.OrganizerSuspended);

        // Act
        await PostHoldAsync(showId, categoryId, 1, "customer-1", "key-1");

        // Assert
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM cancelled_shows WHERE show_id = @s", showId));
    }

    [Fact]
    public async Task CreateHold_ParallelRequestsAfterSuspension_AreAllRejectedAndStockIsIntact()
    {
        // Arrange: 50 customers hold before the suspension, 50 more try afterwards.
        var (showId, categoryId) = await SeedAsync(capacity: 1000);
        var before = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => PostHoldAsync(showId, categoryId, 1, $"early-{i}", $"early-key-{i}")));
        Catalog.Set(showId, SalesEligibilityStatus.OrganizerSuspended);

        // Act
        var after = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => PostHoldAsync(showId, categoryId, 1, $"late-{i}", $"late-key-{i}")));

        // Assert
        Assert.All(before, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.All(after, r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(950, await ScalarAsync("SELECT available FROM stock WHERE show_id = @s", showId));
        Assert.Equal(50, await ScalarAsync("SELECT COUNT(*) FROM holds WHERE show_id = @s", showId));
    }

    [Fact]
    public async Task CreateHold_SuspendAndReinstateWhileHoldsArrive_NeverOversellsAndAccountingBalances()
    {
        // Arrange: 80 customers race for 40 tickets while the organizer flips state repeatedly.
        var (showId, categoryId) = await SeedAsync(capacity: 40);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holds = Enumerable.Range(0, 80).Select(async i =>
        {
            await gate.Task;
            return await PostHoldAsync(showId, categoryId, 1, $"racer-{i}", $"racer-key-{i}");
        }).ToArray();
        var flipper = Task.Run(async () =>
        {
            await gate.Task;
            for (var i = 0; i < 40; i++)
            {
                Catalog.Set(showId, i % 2 == 0 ? SalesEligibilityStatus.OrganizerSuspended : SalesEligibilityStatus.Eligible);
                await Task.Yield();
            }

            Catalog.Set(showId, SalesEligibilityStatus.Eligible);
        });

        // Act
        gate.SetResult();
        var responses = await Task.WhenAll(holds);
        await flipper;

        // Assert
        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var rejected = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(80, created + rejected);
        Assert.InRange(created, 0, 40);
        Assert.Equal(created, await ScalarAsync("SELECT COUNT(*) FROM holds WHERE show_id = @s", showId));
        Assert.Equal(40 - created, await ScalarAsync("SELECT available FROM stock WHERE show_id = @s", showId));
        Assert.Equal(created, await ScalarAsync("SELECT COALESCE(SUM(hi.quantity), 0) FROM hold_items hi JOIN holds h ON h.id = hi.hold_id WHERE h.show_id = @s", showId));
    }

    private async Task<HttpResponseMessage> PostHoldAsync(Guid showId, Guid categoryId, int quantity, string customerSub, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/holds")
        {
            Content = JsonContent.Create(new { showId, items = new[] { new { categoryId, quantity } } })
        };
        // Customers and keys are namespaced per test run so tests sharing one database never collide.
        request.Headers.Add(CustomerTestAuthHandler.SubHeaderName, $"{_run}-{customerSub}");
        request.Headers.Add("Idempotency-Key", $"{_run}-{key}");
        return await _fixture.Factory.CreateClient().SendAsync(request);
    }

    private async Task<HttpResponseMessage> InternalPatchAsync(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, url);
        request.Headers.Add(InternalTestAuthHandler.SubHeaderName, "booking-service");
        request.Headers.Add(InternalTestAuthHandler.ScopeHeaderName, "inventory:write");
        request.Headers.Add(InternalTestAuthHandler.AutHeaderName, "APPLICATION");
        return await _fixture.Factory.CreateClient().SendAsync(request);
    }

    private async Task<(Guid ShowId, Guid CategoryId)> SeedAsync(int capacity)
    {
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var rules = new NpgsqlCommand(
            "INSERT INTO show_rules (show_id, organizer_id, max_per_customer, hold_minutes, high_demand) VALUES (@s, @o, 1000, 10, false);", connection);
        rules.Parameters.AddWithValue("s", showId);
        rules.Parameters.AddWithValue("o", Guid.NewGuid());
        await rules.ExecuteNonQueryAsync();
        await using var stock = new NpgsqlCommand(
            "INSERT INTO stock (show_id, category_id, capacity, available, unit_price, currency) VALUES (@s, @c, @cap, @cap, 50.00, 'LKR');", connection);
        stock.Parameters.AddWithValue("s", showId);
        stock.Parameters.AddWithValue("c", categoryId);
        stock.Parameters.AddWithValue("cap", capacity);
        await stock.ExecuteNonQueryAsync();
        return (showId, categoryId);
    }

    private async Task<long> ScalarAsync(string sql, Guid showId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("s", showId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task ExecuteAsync(string sql, Guid showId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("s", showId);
        await command.ExecuteNonQueryAsync();
    }
}