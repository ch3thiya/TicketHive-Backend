using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using BuildingBlocks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace BuildingBlocks.Tests.Integration;

[Collection("Postgres")]
public class HealthChecksIntegrationTests
{
    private readonly PostgresFixture _db;

    public HealthChecksIntegrationTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task Ready_ReturnsHealthy_WhenDatabaseReachable()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["ConnectionStrings:DefaultConnection"] = _db.ConnectionString;
        builder.AddServiceDefaults();

        await using var app = builder.Build();
        app.UseServiceDefaults();
        app.MapDefaultEndpoints();
        await app.StartAsync();
        var client = app.GetTestClient();

        // Act
        var response = await client.GetAsync("/health/ready");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
