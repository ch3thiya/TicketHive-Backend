using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using BuildingBlocks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BuildingBlocks.Tests;

public class ExceptionHandlingTests
{
    [Fact]
    public async Task UnhandledException_ReturnsProblemDetails_WithoutExceptionMessageOrStackTrace()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseTestServer();
        builder.AddServiceDefaults();

        await using var app = builder.Build();
        app.UseServiceDefaults();
        app.MapDefaultEndpoints();
        app.MapGet("/throw", () =>
        {
            throw new InvalidOperationException("sensitive detail that must not leak");
        });

        await app.StartAsync();
        var client = app.GetTestClient();

        // Act
        var response = await client.GetAsync("/throw");
        var body = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType?.ToString() ?? string.Empty);
        Assert.DoesNotContain("sensitive detail", body);
        Assert.DoesNotContain("InvalidOperationException", body);
        Assert.DoesNotContain("at BuildingBlocks.Tests", body);
    }
}
