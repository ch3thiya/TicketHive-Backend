using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using BuildingBlocks;
using Identity.Service.Clients;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Identity.Service.Tests;

public class ScimClientResilienceTests
{
    /// <summary>
    /// Registers IWso2ScimClient the same way Identity's Program.cs does
    /// (AddServiceDefaults() before AddHttpClient&lt;IWso2ScimClient, Wso2ScimClient&gt;
    /// with a custom primary handler) to confirm the standard resilience handler
    /// from BuildingBlocks already wraps it via ConfigureHttpClientDefaults, and
    /// that it does not retry an unsafe PATCH.
    /// </summary>
    [Fact]
    public async Task ScimHttpClient_RetriesGet_ButNotPatch()
    {
        // Arrange - a stub server that always fails, counting attempts per method.
        var attemptCounts = new ConcurrentDictionary<string, int>();
        var stubBuilder = WebApplication.CreateBuilder();
        stubBuilder.WebHost.UseTestServer();
        await using var stub = stubBuilder.Build();
        stub.MapMethods("/", new[] { "GET", "PATCH" }, (HttpContext ctx) =>
        {
            attemptCounts.AddOrUpdate(ctx.Request.Method, 1, (_, count) => count + 1);
            return Results.StatusCode(500);
        });
        await stub.StartAsync();

        var clientBuilder = WebApplication.CreateBuilder();
        clientBuilder.AddServiceDefaults();
        clientBuilder.Services.AddHttpClient<IWso2ScimClient, Wso2ScimClient>(client =>
        {
            client.BaseAddress = new Uri("http://stub");
        })
        .ConfigurePrimaryHttpMessageHandler(() => stub.GetTestServer().CreateHandler());

        await using var clientApp = clientBuilder.Build();
        var httpClient = clientApp.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(IWso2ScimClient));

        // Act
        var patchResponse = await httpClient.PatchAsync("/", new StringContent(string.Empty));

        var getTask = httpClient.GetAsync("/");
        _ = getTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.ExecuteSynchronously);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (attemptCounts.GetValueOrDefault("GET") < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, patchResponse.StatusCode);
        Assert.Equal(1, attemptCounts["PATCH"]);
        Assert.True(
            attemptCounts.GetValueOrDefault("GET") >= 2,
            "Expected the safe GET method to be retried at least once.");
    }
}
