using System.Net;
using Gateway.Proxy;
using Microsoft.AspNetCore.Http;
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

    [Theory]
    [InlineData("GET", "/api/payment/notify", false)]
    [InlineData("POST", "/api/payment/notify", true)]
    [InlineData("POST", "/api/booking/orders", false)]
    public void OnlyPostPaymentNotificationsAreExempt(string method, string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        Assert.Equal(expected, ClientRateLimiter.IsExempt(context.Request));
    }

    [Fact]
    public void NonExemptRateLimitIsPartitionedByRemoteAddress()
    {
        using var limiter = ClientRateLimiter.Create(new ClientRateLimitOptions
        {
            PermitLimit = 1,
            WindowSeconds = 60
        });

        var first = new DefaultHttpContext();
        first.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        first.Request.Path = "/api/catalog/events";
        var other = new DefaultHttpContext();
        other.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.2");
        other.Request.Path = "/api/catalog/events";

        Assert.True(limiter.AttemptAcquire(first).IsAcquired);
        Assert.False(limiter.AttemptAcquire(first).IsAcquired);
        Assert.True(limiter.AttemptAcquire(other).IsAcquired);
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
