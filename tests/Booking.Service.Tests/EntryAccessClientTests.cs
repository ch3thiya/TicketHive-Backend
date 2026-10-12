using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;
using Booking.Service.Clients;

namespace Booking.Service.Tests;

public class EntryAccessClientTests
{
    private static EntryAccessClient CreateClient(Mock<HttpMessageHandler> handler) =>
        new(new HttpClient(handler.Object) { BaseAddress = new Uri("http://catalog.local/") }, NullLogger<EntryAccessClient>.Instance);

    private static Mock<HttpMessageHandler> Returning(HttpStatusCode status, string? json = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(status)
            {
                Content = json is null ? null : new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        return handler;
    }

    [Fact]
    public async Task CheckAsync_Allowed_ReturnsAllowedAndEscapesTheSubjectInTheQuery()
    {
        // Arrange
        HttpRequestMessage? captured = null;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"showId":"11111111-1111-1111-1111-111111111111","allowed":true,"reason":"None"}""", System.Text.Encoding.UTF8, "application/json")
            });
        var showId = Guid.NewGuid();

        // Act
        var decision = await CreateClient(handler).CheckAsync(showId, "a b&c=d");

        // Assert
        Assert.Equal(EntryAccessDecision.Allowed, decision);
        Assert.Equal($"/internal/catalog/shows/{showId}/entry-access", captured!.RequestUri!.AbsolutePath);
        Assert.Equal("?sub=a%20b%26c%3Dd", captured.RequestUri.Query);
    }

    [Fact]
    public async Task CheckAsync_NotAllowed_ReturnsDenied()
    {
        // Arrange
        var handler = Returning(HttpStatusCode.OK, """{"showId":"11111111-1111-1111-1111-111111111111","allowed":false,"reason":"NotShowOwner"}""");

        // Act
        var decision = await CreateClient(handler).CheckAsync(Guid.NewGuid(), "someone");

        // Assert
        Assert.Equal(EntryAccessDecision.Denied, decision);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task CheckAsync_AnyErrorStatus_FailsClosedAsUnavailable(HttpStatusCode status)
    {
        // Act
        var decision = await CreateClient(Returning(status)).CheckAsync(Guid.NewGuid(), "someone");

        // Assert
        Assert.Equal(EntryAccessDecision.Unavailable, decision);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task CheckAsync_UnreadableBody_FailsClosedAsUnavailable(string body)
    {
        // Act
        var decision = await CreateClient(Returning(HttpStatusCode.OK, body)).CheckAsync(Guid.NewGuid(), "someone");

        // Assert
        Assert.Equal(EntryAccessDecision.Unavailable, decision);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TimeoutException))]
    public async Task CheckAsync_TransportOrResilienceFailure_FailsClosedAsUnavailable(Type exceptionType)
    {
        // Arrange: the resilience pipeline throws its own exception types on timeouts and open circuits.
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync((Exception)Activator.CreateInstance(exceptionType, "boom")!);

        // Act
        var decision = await CreateClient(handler).CheckAsync(Guid.NewGuid(), "someone");

        // Assert
        Assert.Equal(EntryAccessDecision.Unavailable, decision);
    }

    [Fact]
    public async Task CheckAsync_CalledTwice_AsksCatalogEachTime()
    {
        // Arrange
        var handler = Returning(HttpStatusCode.OK, """{"showId":"11111111-1111-1111-1111-111111111111","allowed":true,"reason":"None"}""");
        var client = CreateClient(handler);
        var showId = Guid.NewGuid();

        // Act
        await client.CheckAsync(showId, "owner");
        await client.CheckAsync(showId, "owner");

        // Assert
        handler.Protected().Verify("SendAsync", Times.Exactly(2), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }
}