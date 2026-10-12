using System;
using System.Collections.Generic;
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
        int listingCacheSeconds = 5)
    {
        var httpClient = new HttpClient(handler.Object) { BaseAddress = new Uri("http://identity.local/") };
        var options = Options.Create(new OrganizerStatusClientOptions { ListingCacheSeconds = listingCacheSeconds });
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

    private static void VerifyCalls(Mock<HttpMessageHandler> handler, int times) =>
        handler.Protected().Verify("SendAsync", Times.Exactly(times), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());

    [Fact]
    public async Task GetOrganizerStatusAsync_ApprovedOrganizer_ReturnsActiveWithOrganizerId()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var handler = MockHandlerReturning(HttpStatusCode.OK, $$"""{"organizerId":"{{organizerId}}","status":"approved"}""");
        var client = CreateClient(handler, new FakeTimeProvider());

        // Act
        var result = await client.GetOrganizerStatusAsync("sub-1");

        // Assert
        Assert.Equal(OrganizerLookupStatus.Active, result.Status);
        Assert.Equal(organizerId, result.OrganizerId);
    }

    [Fact]
    public async Task GetOrganizerStatusAsync_SuspendedOrganizer_ReturnsSuspendedWithOrganizerId()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var handler = MockHandlerReturning(HttpStatusCode.OK, $$"""{"organizerId":"{{organizerId}}","status":"suspended"}""");
        var client = CreateClient(handler, new FakeTimeProvider());

        // Act
        var result = await client.GetOrganizerStatusAsync("sub-1");

        // Assert
        Assert.Equal(OrganizerLookupStatus.Suspended, result.Status);
        Assert.Equal(organizerId, result.OrganizerId);
    }

    [Theory]
    [InlineData("""{"organizerId":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""{"organizerId":"11111111-1111-1111-1111-111111111111","status":"rejected"}""")]
    [InlineData("""{"organizerId":"11111111-1111-1111-1111-111111111111","status":""}""")]
    public async Task GetOrganizerStatusAsync_MissingOrUnknownStatus_FailsClosedAsUnavailable(string body)
    {
        // Arrange
        var handler = MockHandlerReturning(HttpStatusCode.OK, body);
        var client = CreateClient(handler, new FakeTimeProvider());

        // Act
        var result = await client.GetOrganizerStatusAsync("sub-1");

        // Assert: an old or unexpected Identity answer must never read as an active organizer.
        Assert.Equal(OrganizerLookupStatus.Unavailable, result.Status);
        Assert.Null(result.OrganizerId);
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
    public async Task GetOrganizerStatusAsync_RepeatedCalls_AlwaysAskIdentity()
    {
        // Arrange: management and hold-time checks are never cached, so a status flip
        // applies to the very next request without restarting anything.
        var organizerId = Guid.NewGuid();
        var handler = MockHandlerReturning(HttpStatusCode.OK, $$"""{"organizerId":"{{organizerId}}","status":"approved"}""");
        var timeProvider = new FakeTimeProvider();
        var client = CreateClient(handler, timeProvider);

        // Act
        await client.GetOrganizerStatusAsync("sub-1");
        await client.GetOrganizerStatusAsync("sub-1");
        await client.GetOrganizerStatusByIdAsync(organizerId);

        // Assert
        VerifyCalls(handler, 3);
    }

    [Fact]
    public async Task GetOrganizerStatusByIdAsync_UsesByIdRoute()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        HttpRequestMessage? captured = null;
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"organizerId":"{{organizerId}}","status":"suspended"}""", System.Text.Encoding.UTF8, "application/json")
            });
        var client = CreateClient(handler, new FakeTimeProvider());

        // Act
        var result = await client.GetOrganizerStatusByIdAsync(organizerId);

        // Assert
        Assert.Equal(OrganizerLookupStatus.Suspended, result.Status);
        Assert.Equal($"/internal/identity/organizers/by-id/{organizerId}", captured!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetOrganizerStatusesAsync_MapsStatusesAndMarksUnknownOrganizersNotFound()
    {
        // Arrange
        var active = Guid.NewGuid();
        var suspended = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var handler = MockHandlerReturning(HttpStatusCode.OK,
            $$"""[{"organizerId":"{{active}}","status":"approved"},{"organizerId":"{{suspended}}","status":"suspended"}]""");
        var client = CreateClient(handler, new FakeTimeProvider());

        // Act
        var statuses = await client.GetOrganizerStatusesAsync([active, suspended, unknown]);

        // Assert
        Assert.Equal(OrganizerLookupStatus.Active, statuses[active]);
        Assert.Equal(OrganizerLookupStatus.Suspended, statuses[suspended]);
        Assert.Equal(OrganizerLookupStatus.NotFound, statuses[unknown]);
    }

    [Fact]
    public async Task GetOrganizerStatusesAsync_WithinCacheWindow_ReusesAnswerThenRefreshesAfterExpiry()
    {
        // Arrange
        var suspended = Guid.NewGuid();
        var handler = MockHandlerReturning(HttpStatusCode.OK, $$"""[{"organizerId":"{{suspended}}","status":"suspended"}]""");
        var timeProvider = new FakeTimeProvider();
        var client = CreateClient(handler, timeProvider, listingCacheSeconds: 5);

        // Act
        await client.GetOrganizerStatusesAsync([suspended]);
        timeProvider.Advance(TimeSpan.FromSeconds(4));
        await client.GetOrganizerStatusesAsync([suspended]);
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        await client.GetOrganizerStatusesAsync([suspended]);

        // Assert: display data lags at most the cache window; a refresh happens right after it.
        VerifyCalls(handler, 2);
    }

    [Fact]
    public async Task GetOrganizerStatusesAsync_IdentityOutage_ReturnsUnavailableAndRetriesAfterCacheWindow()
    {
        // Arrange
        var id = Guid.NewGuid();
        var handler = MockHandlerReturning(HttpStatusCode.ServiceUnavailable);
        var timeProvider = new FakeTimeProvider();
        var client = CreateClient(handler, timeProvider, listingCacheSeconds: 5);

        // Act
        var first = await client.GetOrganizerStatusesAsync([id]);
        var second = await client.GetOrganizerStatusesAsync([id]);
        timeProvider.Advance(TimeSpan.FromSeconds(6));
        await client.GetOrganizerStatusesAsync([id]);

        // Assert: one call per window, so an outage cannot slow every public listing request.
        Assert.Equal(OrganizerLookupStatus.Unavailable, first[id]);
        Assert.Equal(OrganizerLookupStatus.Unavailable, second[id]);
        VerifyCalls(handler, 2);
    }

    [Fact]
    public async Task GetOrganizerStatusAsync_ResiliencePipelineTimeoutOrOpenCircuit_ReturnsUnavailable()
    {
        // Arrange: Polly's timeout and open-circuit exceptions are not HttpRequestException.
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TimeoutException("circuit open"));
        var client = CreateClient(handler, new FakeTimeProvider());

        // Act
        var bySub = await client.GetOrganizerStatusAsync("sub-1");
        var byId = await client.GetOrganizerStatusByIdAsync(Guid.NewGuid());
        var batch = await client.GetOrganizerStatusesAsync([Guid.NewGuid()]);

        // Assert
        Assert.Equal(OrganizerLookupStatus.Unavailable, bySub.Status);
        Assert.Equal(OrganizerLookupStatus.Unavailable, byId.Status);
        Assert.Equal(OrganizerLookupStatus.Unavailable, Assert.Single(batch).Value);
    }
}