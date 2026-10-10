using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Booking.Service.Db;
using Booking.Service.Models;
using Booking.Service.Tests.Integration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Booking.Service.Tests;

[Collection("Postgres")]
public class CancellationAuthorizationApiTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Customer_cancel_and_status_require_authentication_and_ownership()
    {
        await using var factory = new BookingCancellationApiFactory(fixture.ConnectionString);
        var client = factory.CreateClient();
        var order = await SeedAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/booking/orders/{order.Id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Request(HttpMethod.Post, $"/api/booking/orders/{order.Id}/cancel", "other"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(Request(HttpMethod.Post, $"/api/booking/orders/{order.Id}/cancel", "owner"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Request(HttpMethod.Get, $"/api/booking/orders/{order.Id}/cancellation", "other"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Get, $"/api/booking/orders/{order.Id}/cancellation", "owner"))).StatusCode);
    }

    [Fact]
    public async Task Internal_show_cancellation_requires_application_scope()
    {
        await using var factory = new BookingCancellationApiFactory(fixture.ConnectionString);
        var client = factory.CreateClient();
        var show = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsync($"/internal/booking/cancellations/shows/{show}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Put, $"/internal/booking/cancellations/shows/{show}", "user", "booking:write", "USER"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Put, $"/internal/booking/cancellations/shows/{show}", "app", "wrong", "APPLICATION"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Request(HttpMethod.Put, $"/internal/booking/cancellations/shows/{show}", "app", "booking:write", "APPLICATION"))).StatusCode);
    }

    private static HttpRequestMessage Request(HttpMethod method, string uri, string sub, string? scope = null, string? aut = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("Test-Sub", sub);
        if (scope is not null) request.Headers.Add("Test-Scope", scope);
        if (aut is not null) request.Headers.Add("Test-Aut", aut);
        return request;
    }

    private async Task<Order> SeedAsync()
    {
        var order = new Order { Id = Guid.NewGuid(), HoldId = Guid.NewGuid(), CustomerSub = "owner", CustomerEmail = "test@example.invalid", CustomerName = "Test", ShowId = Guid.NewGuid(), Status = OrderStatus.Confirmed, TotalAmount = 100, Currency = "LKR", IdempotencyKey = Guid.NewGuid().ToString(), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        await new OrderRepository(new DbConnectionFactory(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = fixture.ConnectionString }).Build())).CreateAsync(order);
        return order;
    }
}

file sealed class BookingCancellationApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders().AddConsole());
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = connectionString }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new RulesHttpClientFactory());
            services.AddAuthentication("CancellationTest").AddScheme<AuthenticationSchemeOptions, BookingTestAuthHandler>("CancellationTest", _ => { });
            services.PostConfigure<AuthenticationOptions>(o => { o.DefaultAuthenticateScheme = "CancellationTest"; o.DefaultChallengeScheme = "CancellationTest"; o.DefaultScheme = "CancellationTest"; });
        });
    }
}

file sealed class RulesHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new RulesHandler()) { BaseAddress = new Uri("http://test/") };
    private sealed class RulesHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"startsAt\":\"2099-01-01T00:00:00Z\",\"status\":\"Published\"}", System.Text.Encoding.UTF8, "application/json") });
    }
}

file sealed class BookingTestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Test-Sub", out var sub)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, sub.ToString()) };
        if (Request.Headers.TryGetValue("Test-Scope", out var scope)) claims.Add(new Claim("scope", scope.ToString()));
        if (Request.Headers.TryGetValue("Test-Aut", out var aut)) claims.Add(new Claim("aut", aut.ToString()));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}
