using System.Net;
using Xunit;

namespace Gateway.Tests;

public class EdgeRoutingTests : IClassFixture<GatewayFactory>
{
    private readonly HttpClient _client;

    public EdgeRoutingTests(GatewayFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Internal_path_is_not_found()
    {
        var response = await _client.GetAsync("/internal/identity/organizers/x");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Protected_route_without_token_is_unauthorized()
    {
        var response = await _client.GetAsync("/api/booking/orders");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_route_does_not_allow_other_methods_without_token()
    {
        var response = await _client.PostAsync("/api/catalog/events", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/booking/orders/0199a1b2-0000-7000-8000-000000000001/confirm-sandbox")]
    [InlineData("/api/payment/confirm-sandbox/0199a1b2-0000-7000-8000-000000000001")]
    public async Task Sandbox_confirmation_requires_token(string path)
    {
        var response = await _client.PostAsync(path, new StringContent("{}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_catalog_events_route_is_forwarded_without_token()
    {
        var response = await _client.GetAsync("/api/catalog/events");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task My_events_route_requires_token()
    {
        var response = await _client.GetAsync("/api/catalog/events/my-events");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Payment_notify_callback_is_forwarded_without_token()
    {
        var response = await _client.PostAsync("/api/payment/notify", new FormUrlEncodedContent([]));

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Preflight_from_allowed_origin_is_answered_without_token()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/booking/orders");
        request.Headers.Add("Origin", GatewayFactory.AllowedOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            GatewayFactory.AllowedOrigin,
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Liveness_probe_is_healthy()
    {
        var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_path_is_not_found()
    {
        var response = await _client.GetAsync("/something-else");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
