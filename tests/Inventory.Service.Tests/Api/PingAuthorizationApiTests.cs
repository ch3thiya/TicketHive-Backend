using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace Inventory.Service.Tests.Api;

public class PingAuthorizationApiTests : IClassFixture<InventoryApiFactory>
{
    private readonly InventoryApiFactory _factory;

    public PingAuthorizationApiTests(InventoryApiFactory factory)
    {
        _factory = factory;
    }

    // GET /api/inventory/ping — customer scheme (AC7)

    [Fact]
    public async Task Ping_NoToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/inventory/ping");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ping_ValidCustomerToken_ReturnsOk()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/inventory/ping");
        request.Headers.Add(CustomerTestAuthHandler.SubHeaderName, "customer-sub");
        request.Headers.Add(CustomerTestAuthHandler.GroupsHeaderName, "Customer");
        var client = _factory.CreateClient();

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // GET /internal/inventory/ping — Internal scheme + InternalService policy (AC6)

    [Fact]
    public async Task InternalPing_NoToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/internal/inventory/ping");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InternalPing_TokenWithoutRequiredScope_ReturnsForbidden()
    {
        // A customer-shaped principal authenticated on the Internal scheme
        // (no "scope" claim at all) — this is what a real customer JWT
        // would look like once the Internal scheme validates it, since
        // audience validation is off for that scheme.
        var request = new HttpRequestMessage(HttpMethod.Get, "/internal/inventory/ping");
        request.Headers.Add(InternalTestAuthHandler.SubHeaderName, "customer-sub");
        var client = _factory.CreateClient();

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task InternalPing_WrongScope_ReturnsForbidden()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/internal/inventory/ping");
        request.Headers.Add(InternalTestAuthHandler.SubHeaderName, "service-client");
        request.Headers.Add(InternalTestAuthHandler.ScopeHeaderName, "catalog:write");
        request.Headers.Add(InternalTestAuthHandler.AutHeaderName, "APPLICATION");
        var client = _factory.CreateClient();

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task InternalPing_TokenWithRequiredScopeAndAut_ReturnsOk()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/internal/inventory/ping");
        request.Headers.Add(InternalTestAuthHandler.SubHeaderName, "service-client");
        request.Headers.Add(InternalTestAuthHandler.ScopeHeaderName, "inventory:write");
        request.Headers.Add(InternalTestAuthHandler.AutHeaderName, "APPLICATION");
        var client = _factory.CreateClient();

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
