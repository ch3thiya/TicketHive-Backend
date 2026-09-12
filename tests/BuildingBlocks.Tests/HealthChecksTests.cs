using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using BuildingBlocks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace BuildingBlocks.Tests;

public class HealthChecksTests
{
    [Fact]
    public async Task Live_ReturnsHealthy_WithNoDatabaseConfigured()
    {
        // Arrange
        await using var app = BuildTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        // Act
        var response = await client.GetAsync("/health/live");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_ReturnsSameStatusAsLive_WithNoDatabaseConfigured()
    {
        // Arrange
        await using var app = BuildTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        // Act
        var liveResponse = await client.GetAsync("/health/live");
        var healthResponse = await client.GetAsync("/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        Assert.Equal(liveResponse.StatusCode, healthResponse.StatusCode);
    }

    [Fact]
    public async Task Ready_ReturnsUnhealthy_WhenDatabaseUnreachable()
    {
        // Arrange — nothing listens on this loopback port, so the connection attempt fails fast.
        await using var app = BuildTestApp(
            "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope;Timeout=1");
        await app.StartAsync();
        var client = app.GetTestClient();

        // Act
        var response = await client.GetAsync("/health/ready");

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private static WebApplication BuildTestApp(string? connectionString = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (connectionString is not null)
        {
            builder.Configuration["ConnectionStrings:DefaultConnection"] = connectionString;
        }

        builder.AddServiceDefaults();

        var app = builder.Build();
        app.UseServiceDefaults();
        app.MapDefaultEndpoints();

        return app;
    }
}
