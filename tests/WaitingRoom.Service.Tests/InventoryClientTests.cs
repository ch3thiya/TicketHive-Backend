using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;
using WaitingRoom.Service.Clients;

namespace WaitingRoom.Service.Tests;

public class InventoryClientTests
{
    private static InventoryClient CreateClient(Mock<HttpMessageHandler> handler)
    {
        var httpClient = new HttpClient(handler.Object) { BaseAddress = new Uri("http://inventory.local/") };
        return new InventoryClient(httpClient, Mock.Of<ILogger<InventoryClient>>());
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
    public async Task IsSoldOutAsync_EveryCategoryAtZero_ReturnsTrue()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var body = $$"""
            {"showId":"{{showId}}","categories":[
                {"categoryId":"{{Guid.NewGuid()}}","capacity":100,"available":0,"unitPrice":10,"currency":"LKR"},
                {"categoryId":"{{Guid.NewGuid()}}","capacity":50,"available":0,"unitPrice":20,"currency":"LKR"}
            ]}
            """;
        var client = CreateClient(MockHandlerReturning(HttpStatusCode.OK, body));

        // Act
        var soldOut = await client.IsSoldOutAsync(showId);

        // Assert
        Assert.True(soldOut);
    }

    [Fact]
    public async Task IsSoldOutAsync_OneCategoryStillHasStock_ReturnsFalse()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var body = $$"""
            {"showId":"{{showId}}","categories":[
                {"categoryId":"{{Guid.NewGuid()}}","capacity":100,"available":0,"unitPrice":10,"currency":"LKR"},
                {"categoryId":"{{Guid.NewGuid()}}","capacity":50,"available":3,"unitPrice":20,"currency":"LKR"}
            ]}
            """;
        var client = CreateClient(MockHandlerReturning(HttpStatusCode.OK, body));

        // Act
        var soldOut = await client.IsSoldOutAsync(showId);

        // Assert
        Assert.False(soldOut);
    }

    [Fact]
    public async Task IsSoldOutAsync_InventoryReturnsServerError_ThrowsInventoryUnavailableException()
    {
        // Arrange
        var client = CreateClient(MockHandlerReturning(HttpStatusCode.ServiceUnavailable));

        // Act & Assert
        await Assert.ThrowsAsync<InventoryUnavailableException>(() => client.IsSoldOutAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task IsSoldOutAsync_InventoryUnreachable_ThrowsInventoryUnavailableExceptionNotHttpRequestException()
    {
        // Arrange
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var client = CreateClient(handler);

        // Act & Assert: a health-check failure must never look like "sold out".
        await Assert.ThrowsAsync<InventoryUnavailableException>(() => client.IsSoldOutAsync(Guid.NewGuid()));
    }
}
