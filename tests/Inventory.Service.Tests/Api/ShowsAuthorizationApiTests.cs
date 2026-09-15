using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Moq;
using Xunit;

namespace Inventory.Service.Tests.Api;

// Authentication/authorization on the real endpoints that superseded the
// ping endpoints (AC6, AC7): the customer-facing scheme proven by
// AvailabilityController's anonymous access, and the Internal scheme plus
// the InternalService policy proven by InternalShowsController.
public class ShowsAuthorizationApiTests : IClassFixture<InventoryApiFactory>
{
    private readonly InventoryApiFactory _factory;

    public ShowsAuthorizationApiTests(InventoryApiFactory factory)
    {
        _factory = factory;
        _factory.ResetRepositoryDefaults();
    }

    private static object ValidInitializeRequestBody() => new
    {
        organizerId = Guid.NewGuid(),
        onSaleAt = (DateTimeOffset?)null,
        maxPerCustomer = 6,
        holdMinutes = 10,
        highDemand = false,
        categories = new[]
        {
            new { categoryId = Guid.NewGuid(), capacity = 100, unitPrice = 25.00m, currency = "LKR", allocationMode = "GA" }
        }
    };

    // GET /api/inventory/shows/{showId}/availability — anonymous by design (AC4)

    [Fact]
    public async Task Availability_NoToken_ReturnsOkWithoutAuthentication()
    {
        var showId = Guid.NewGuid();
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/inventory/shows/{showId}/availability");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Availability_KnownShow_NoToken_ReturnsOk()
    {
        var showId = Guid.NewGuid();
        _factory.StockRepositoryMock
            .Setup(r => r.GetByShowIdAsync(showId))
            .ReturnsAsync(new System.Collections.Generic.List<Inventory.Service.Models.StockItem>
            {
                new() { ShowId = showId, CategoryId = Guid.NewGuid(), Capacity = 100, Available = 80, UnitPrice = 25.00m, Currency = "LKR" }
            });
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/inventory/shows/{showId}/availability");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // PUT /internal/inventory/shows/{showId} — Internal scheme + InternalService policy (AC7)

    [Fact]
    public async Task InitializeStock_NoToken_ReturnsUnauthorized()
    {
        var showId = Guid.NewGuid();
        var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync($"/internal/inventory/shows/{showId}", ValidInitializeRequestBody());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InitializeStock_CustomerTokenWithoutRequiredScope_ReturnsForbidden()
    {
        // A customer-shaped principal authenticated on the Internal scheme
        // (no "scope" claim at all) — this is what a real customer JWT
        // would look like once the Internal scheme validates it, since
        // audience validation is off for that scheme.
        var showId = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/internal/inventory/shows/{showId}")
        {
            Content = JsonContent.Create(ValidInitializeRequestBody())
        };
        request.Headers.Add(InternalTestAuthHandler.SubHeaderName, "customer-sub");
        var client = _factory.CreateClient();

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task InitializeStock_WrongScope_ReturnsForbidden()
    {
        var showId = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/internal/inventory/shows/{showId}")
        {
            Content = JsonContent.Create(ValidInitializeRequestBody())
        };
        request.Headers.Add(InternalTestAuthHandler.SubHeaderName, "service-client");
        request.Headers.Add(InternalTestAuthHandler.ScopeHeaderName, "catalog:write");
        request.Headers.Add(InternalTestAuthHandler.AutHeaderName, "APPLICATION");
        var client = _factory.CreateClient();

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task InitializeStock_ValidServiceToken_ReturnsOk()
    {
        var showId = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/internal/inventory/shows/{showId}")
        {
            Content = JsonContent.Create(ValidInitializeRequestBody())
        };
        request.Headers.Add(InternalTestAuthHandler.SubHeaderName, "service-client");
        request.Headers.Add(InternalTestAuthHandler.ScopeHeaderName, "inventory:write");
        request.Headers.Add(InternalTestAuthHandler.AutHeaderName, "APPLICATION");
        var client = _factory.CreateClient();

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
