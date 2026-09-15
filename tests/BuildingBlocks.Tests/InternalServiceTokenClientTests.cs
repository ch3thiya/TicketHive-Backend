using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Moq.Protected;
using Xunit;

namespace BuildingBlocks.Tests;

public class InternalServiceTokenClientTests
{
    private static InternalServiceTokenClientOptions Options() => new()
    {
        TokenEndpoint = "https://identity.local/oauth2/token",
        ClientId = "inventory-client",
        ClientSecret = "super-secret",
        Scope = "inventory:write"
    };

    private static InternalServiceTokenClient CreateClient(Mock<HttpMessageHandler> handler, FakeTimeProvider timeProvider)
    {
        var httpClient = new HttpClient(handler.Object);
        return new InternalServiceTokenClient(httpClient, timeProvider, Microsoft.Extensions.Options.Options.Create(Options()), Mock.Of<ILogger<InternalServiceTokenClient>>());
    }

    private static Mock<HttpMessageHandler> MockHandlerReturningToken(string accessToken, int expiresIn)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"access_token":"{{accessToken}}","expires_in":{{expiresIn}}}""", System.Text.Encoding.UTF8, "application/json")
            });
        return handler;
    }

    [Fact]
    public async Task GetAccessTokenAsync_FirstCall_FetchesTokenFromEndpoint()
    {
        // Arrange
        var handler = MockHandlerReturningToken("token-1", expiresIn: 3600);
        var client = CreateClient(handler, new FakeTimeProvider());

        // Act
        var token = await client.GetAccessTokenAsync();

        // Assert
        Assert.Equal("token-1", token);
        handler.Protected().Verify("SendAsync", Times.Once(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetAccessTokenAsync_SecondCallWithinExpiry_ReusesCachedToken()
    {
        // Arrange
        var handler = MockHandlerReturningToken("token-1", expiresIn: 3600);
        var timeProvider = new FakeTimeProvider();
        var client = CreateClient(handler, timeProvider);

        // Act
        await client.GetAccessTokenAsync();
        timeProvider.Advance(TimeSpan.FromMinutes(30));
        var second = await client.GetAccessTokenAsync();

        // Assert
        Assert.Equal("token-1", second);
        handler.Protected().Verify("SendAsync", Times.Once(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetAccessTokenAsync_AfterExpiry_FetchesNewToken()
    {
        // Arrange
        var handler = new Mock<HttpMessageHandler>();
        var callCount = 0;
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                var token = $"token-{callCount}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"access_token":"{{token}}","expires_in":3600}""", System.Text.Encoding.UTF8, "application/json")
                };
            });
        var timeProvider = new FakeTimeProvider();
        var client = CreateClient(handler, timeProvider);

        // Act
        var first = await client.GetAccessTokenAsync();
        timeProvider.Advance(TimeSpan.FromHours(2));
        var second = await client.GetAccessTokenAsync();

        // Assert
        Assert.Equal("token-1", first);
        Assert.Equal("token-2", second);
        handler.Protected().Verify("SendAsync", Times.Exactly(2), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetAccessTokenAsync_NonSuccessResponse_ThrowsClearError()
    {
        // Arrange
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = CreateClient(handler, new FakeTimeProvider());

        // Act & Assert: a failure must surface as a clear error, never a null/empty token.
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAccessTokenAsync());
    }

    [Fact]
    public async Task GetAccessTokenAsync_EndpointUnreachable_ThrowsClearError()
    {
        // Arrange
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var client = CreateClient(handler, new FakeTimeProvider());

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAccessTokenAsync());
    }

    [Fact]
    public async Task GetAccessTokenAsync_ResponseMissingAccessToken_ThrowsClearError()
    {
        // Arrange
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"expires_in":3600}""", System.Text.Encoding.UTF8, "application/json")
            });
        var client = CreateClient(handler, new FakeTimeProvider());

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAccessTokenAsync());
    }
}
