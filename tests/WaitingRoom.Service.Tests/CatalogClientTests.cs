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
using WaitingRoom.Service.Clients;

namespace WaitingRoom.Service.Tests;

public class CatalogClientTests
{
    private static CatalogClient CreateClient(
        Mock<HttpMessageHandler> handler,
        FakeTimeProvider timeProvider,
        int cacheDurationSeconds = 30)
    {
        var httpClient = new HttpClient(handler.Object) { BaseAddress = new Uri("http://catalog.local/") };
        var options = Options.Create(new CatalogClientOptions { CacheDurationSeconds = cacheDurationSeconds });
        return new CatalogClient(httpClient, new MemoryCache(new MemoryCacheOptions()), timeProvider, options, Mock.Of<ILogger<CatalogClient>>());
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
    public async Task GetSalesRulesAsync_HighDemandShow_ReturnsOnSaleAtAndHighDemand()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var onSaleAt = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var body = $$"""{"showId":"{{showId}}","onSaleAt":"{{onSaleAt:O}}","highDemand":true}""";
        var client = CreateClient(MockHandlerReturning(HttpStatusCode.OK, body), new FakeTimeProvider());

        // Act
        var result = await client.GetSalesRulesAsync(showId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(onSaleAt, result!.OnSaleAt);
        Assert.True(result.HighDemand);
    }

    [Fact]
    public async Task GetSalesRulesAsync_UnknownShow_ReturnsNull()
    {
        // Arrange
        var client = CreateClient(MockHandlerReturning(HttpStatusCode.NotFound), new FakeTimeProvider());

        // Act
        var result = await client.GetSalesRulesAsync(Guid.NewGuid());

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetSalesRulesAsync_CatalogReturnsServerError_ThrowsCatalogUnavailableException()
    {
        // Arrange
        var client = CreateClient(MockHandlerReturning(HttpStatusCode.ServiceUnavailable), new FakeTimeProvider());

        // Act & Assert
        await Assert.ThrowsAsync<CatalogUnavailableException>(() => client.GetSalesRulesAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetSalesRulesAsync_CatalogUnreachable_ThrowsCatalogUnavailableExceptionNotHttpRequestException()
    {
        // Arrange
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var client = CreateClient(handler, new FakeTimeProvider());

        // Act & Assert
        await Assert.ThrowsAsync<CatalogUnavailableException>(() => client.GetSalesRulesAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetSalesRulesAsync_SecondCallWithinCacheWindow_DoesNotCallHttpAgain()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var body = $$"""{"showId":"{{showId}}","onSaleAt":null,"highDemand":false}""";
        var handler = MockHandlerReturning(HttpStatusCode.OK, body);
        var timeProvider = new FakeTimeProvider();
        var client = CreateClient(handler, timeProvider, cacheDurationSeconds: 30);

        // Act
        await client.GetSalesRulesAsync(showId);
        timeProvider.Advance(TimeSpan.FromSeconds(15));
        await client.GetSalesRulesAsync(showId);

        // Assert
        handler.Protected().Verify("SendAsync", Times.Once(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }
}
