using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Moq.Protected;
using Xunit;
using Catalog.Service.Clients;

namespace Catalog.Service.Tests;

public class OrganizerStatusClientTests
{
    private static OrganizerStatusClient CreateClient(
        Mock<HttpMessageHandler> handler,
        FakeTimeProvider timeProvider,
        int cacheDurationSeconds = 60)
    {
        var httpClient = new HttpClient(handler.Object) { BaseAddress = new Uri("http://identity.local/") };
        var options = Options.Create(new OrganizerStatusClientOptions { CacheDurationSeconds = cacheDurationSeconds });
        return new OrganizerStatusClient(httpClient, new MemoryCache(new MemoryCacheOptions()), timeProvider, options, Mock.Of<ILogger<OrganizerStatusClient>>());
    }

    private static Mock<HttpMessageHandler> MockHandlerReturning(HttpStatusCode statusCode, string? jsonBody = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(statusCode)
            {
                Content = jsonBody is null ? null : new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json")
            });
        return handler;
    }

    [Fact]
    public async Task GetOrganizerStatusAsync_ApprovedOrganizer_ReturnsActiveWithOrganizerId()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var handler = MockHandlerReturning(HttpStatusCode.OK, $$"""{"organizerId":"{{organizerId}}"}""");
        var client = CreateClient(handler, new FakeTimeProvider());

        // Act
        var result = await client.GetOrganizerStatusAsync("sub-1");

        // Assert
        Assert.Equal(OrganizerLookupStatus.Active, result.Status);
        Assert.Equal(organizerId, result.OrganizerId);
    }

    [Fact]
    public async Task GetOrganizerStatusAsync_NonOrganizer_ReturnsNotFound()
    {
        // Arrange
        var handler = MockHandlerReturning(HttpStatusCode.NotFound);
        var client = CreateClient(handler, new FakeTimeProvider());

        // Act
        var result = await client.GetOrganizerStatusAsync("sub-customer");

        // Assert
        Assert.Equal(OrganizerLookupStatus.NotFound, result.Status);
        Assert.Null(result.OrganizerId);
    }

    [Fact]
    public async Task GetOrganizerStatusAsync_IdentityReturnsServerError_ReturnsUnavailable()
    {
        // Arrange
        var handler = MockHandlerReturning(HttpStatusCode.ServiceUnavailable);
        var client = CreateClient(handler, new FakeTimeProvider());

        // Act
        var result = await client.GetOrganizerStatusAsync("sub-1");

        // Assert
        Assert.Equal(OrganizerLookupStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task GetOrganizerStatusAsync_IdentityUnreachable_ReturnsUnavailableNotNotFound()
    {
        // Arrange
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var client = CreateClient(handler, new FakeTimeProvider());

        // Act
        var result = await client.GetOrganizerStatusAsync("sub-1");

        // Assert: a network failure must never be mistaken for "not an organizer".
        Assert.Equal(OrganizerLookupStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task GetOrganizerStatusAsync_SecondCallWithinCacheWindow_DoesNotCallHttpAgain()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var handler = MockHandlerReturning(HttpStatusCode.OK, $$"""{"organizerId":"{{organizerId}}"}""");
        var timeProvider = new FakeTimeProvider();
        var client = CreateClient(handler, timeProvider, cacheDurationSeconds: 60);

        // Act
        await client.GetOrganizerStatusAsync("sub-1");
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        var second = await client.GetOrganizerStatusAsync("sub-1");

        // Assert
        Assert.Equal(OrganizerLookupStatus.Active, second.Status);
        handler.Protected().Verify("SendAsync", Times.Once(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetOrganizerStatusAsync_AfterCacheWindowElapses_CallsHttpAgain()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var handler = MockHandlerReturning(HttpStatusCode.OK, $$"""{"organizerId":"{{organizerId}}"}""");
        var timeProvider = new FakeTimeProvider();
        var client = CreateClient(handler, timeProvider, cacheDurationSeconds: 60);

        // Act
        await client.GetOrganizerStatusAsync("sub-1");
        timeProvider.Advance(TimeSpan.FromSeconds(61));
        await client.GetOrganizerStatusAsync("sub-1");

        // Assert
        handler.Protected().Verify("SendAsync", Times.Exactly(2), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetOrganizerStatusAsync_DifferentSubjects_AreCachedIndependently()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var handler = MockHandlerReturning(HttpStatusCode.OK, $$"""{"organizerId":"{{organizerId}}"}""");
        var client = CreateClient(handler, new FakeTimeProvider());

        // Act
        await client.GetOrganizerStatusAsync("sub-1");
        await client.GetOrganizerStatusAsync("sub-2");

        // Assert
        handler.Protected().Verify("SendAsync", Times.Exactly(2), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }
}
