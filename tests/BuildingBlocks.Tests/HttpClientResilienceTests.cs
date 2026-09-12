using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using BuildingBlocks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuildingBlocks.Tests;

public class HttpClientResilienceTests
{
    [Fact]
    public async Task StandardResilienceHandler_RetriesSafeGet_ButNotUnsafePost()
    {
        // Arrange — a stub server that always fails, counting attempts per method.
        var attemptCounts = new ConcurrentDictionary<string, int>();
        var stubBuilder = WebApplication.CreateBuilder();
        stubBuilder.WebHost.UseTestServer();
        await using var stub = stubBuilder.Build();
        stub.MapMethods("/", new[] { "GET", "POST" }, (HttpContext ctx) =>
        {
            attemptCounts.AddOrUpdate(ctx.Request.Method, 1, (_, count) => count + 1);
            return Results.StatusCode(500);
        });
        await stub.StartAsync();

        var clientBuilder = WebApplication.CreateBuilder();
        clientBuilder.AddServiceDefaults();
        clientBuilder.Services.AddHttpClient("stub", c => c.BaseAddress = new Uri("http://stub"))
            .ConfigurePrimaryHttpMessageHandler(() => stub.GetTestServer().CreateHandler());
        await using var clientApp = clientBuilder.Build();
        var httpClient = clientApp.Services.GetRequiredService<IHttpClientFactory>().CreateClient("stub");

        // Act
        var postResponse = await httpClient.PostAsync("/", new StringContent(string.Empty));

        var getTask = httpClient.GetAsync("/");
        _ = getTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.ExecuteSynchronously);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (attemptCounts.GetValueOrDefault("GET") < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, postResponse.StatusCode);
        Assert.Equal(1, attemptCounts["POST"]);
        Assert.True(
            attemptCounts.GetValueOrDefault("GET") >= 2,
            "Expected the safe GET method to be retried at least once.");
    }
}
