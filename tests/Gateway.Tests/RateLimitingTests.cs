using System.Net;
using Xunit;

namespace Gateway.Tests;

public class RateLimitingTests : IClassFixture<GatewayFactory>
{
    private const int PermitLimit = 2;

    private readonly GatewayFactory _factory;

    public RateLimitingTests(GatewayFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Requests_over_the_limit_are_rejected_with_429()
    {
        using var client = CreateLimitedClient();

        for (var i = 0; i < PermitLimit; i++)
        {
            var allowed = await client.GetAsync("/something-else");
            Assert.Equal(HttpStatusCode.NotFound, allowed.StatusCode);
        }

        var rejected = await client.GetAsync("/something-else");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Health_probes_are_not_rate_limited()
    {
        using var client = CreateLimitedClient();

        for (var i = 0; i < PermitLimit * 3; i++)
        {
            var response = await client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Payment_notify_callback_is_not_rate_limited()
    {
        using var client = CreateLimitedClient();

        for (var i = 0; i < PermitLimit * 3; i++)
        {
            var response = await client.PostAsync("/api/payment/notify", new FormUrlEncodedContent([]));
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }
    }

    // Each test gets its own host, so windows never carry over between tests.
    private HttpClient CreateLimitedClient() =>
        _factory
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("RateLimiting:PermitLimit", PermitLimit.ToString());
                builder.UseSetting("RateLimiting:WindowSeconds", "60");
            })
            .CreateClient();
}
