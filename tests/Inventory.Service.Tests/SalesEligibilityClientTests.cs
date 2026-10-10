using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;
using Inventory.Service.Clients;
using Inventory.Service.Models;

namespace Inventory.Service.Tests;

public class SalesEligibilityClientTests
{
    private static SalesEligibilityClient CreateClient(Mock<HttpMessageHandler> handler) =>
        new(new HttpClient(handler.Object) { BaseAddress = new Uri("http://catalog.local/") }, NullLogger<SalesEligibilityClient>.Instance);

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
    public async Task CheckAsync_EligibleAnswer_ReturnsEligible()
    {
        // Arrange
        var client = CreateClient(Returning(HttpStatusCode.OK, """{"showId":"11111111-1111-1111-1111-111111111111","eligible":true,"reason":"None"}"""));

        // Act
        var decision = await client.CheckAsync(Guid.NewGuid());

        // Assert
        Assert.True(decision.IsEligible);
    }

    [Fact]
    public async Task CheckAsync_OrganizerSuspendedReason_ReturnsOrganizerSuspended()
    {
        // Arrange
        var client = CreateClient(Returning(HttpStatusCode.OK, """{"showId":"11111111-1111-1111-1111-111111111111","eligible":false,"reason":"OrganizerSuspended"}"""));

        // Act
        var decision = await client.CheckAsync(Guid.NewGuid());

        // Assert
        Assert.Equal(SalesEligibilityStatus.OrganizerSuspended, decision.Status);
    }

    [Theory]
    [InlineData("ShowNotOnSale")]
    [InlineData("OrganizerNotActive")]
    [InlineData("SomethingNew")]
    public async Task CheckAsync_OtherIneligibleReasons_ReturnNotOnSale(string reason)
    {
        // Arrange
        var client = CreateClient(Returning(HttpStatusCode.OK, $$"""{"showId":"11111111-1111-1111-1111-111111111111","eligible":false,"reason":"{{reason}}"}"""));

        // Act
        var decision = await client.CheckAsync(Guid.NewGuid());

        // Assert
        Assert.Equal(SalesEligibilityStatus.NotOnSale, decision.Status);
    }

    [Fact]
    public async Task CheckAsync_ShowUnknownToCatalog_ReturnsNotOnSale()
    {
        // Arrange
        var client = CreateClient(Returning(HttpStatusCode.NotFound));

        // Act
        var decision = await client.CheckAsync(Guid.NewGuid());

        // Assert
        Assert.Equal(SalesEligibilityStatus.NotOnSale, decision.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task CheckAsync_CatalogErrorOrRejectedToken_FailsClosedAsUnavailable(HttpStatusCode status)
    {
        // Arrange
        var client = CreateClient(Returning(status));

        // Act
        var decision = await client.CheckAsync(Guid.NewGuid());

        // Assert
        Assert.Equal(SalesEligibilityStatus.Unavailable, decision.Status);
    }

    [Fact]
    public async Task CheckAsync_CatalogUnreachable_FailsClosedAsUnavailable()
    {
        // Arrange
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var client = CreateClient(handler);

        // Act
        var decision = await client.CheckAsync(Guid.NewGuid());

        // Assert
        Assert.Equal(SalesEligibilityStatus.Unavailable, decision.Status);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task CheckAsync_UnreadableBody_FailsClosedAsUnavailable(string body)
    {
        // Arrange
        var client = CreateClient(Returning(HttpStatusCode.OK, body));

        // Act
        var decision = await client.CheckAsync(Guid.NewGuid());

        // Assert
        Assert.Equal(SalesEligibilityStatus.Unavailable, decision.Status);
    }

    [Fact]
    public async Task CheckAsync_CalledTwice_AsksCatalogEachTime()
    {
        // Arrange
        var handler = Returning(HttpStatusCode.OK, """{"showId":"11111111-1111-1111-1111-111111111111","eligible":true,"reason":"None"}""");
        var client = CreateClient(handler);
        var showId = Guid.NewGuid();

        // Act
        await client.CheckAsync(showId);
        await client.CheckAsync(showId);

        // Assert
        handler.Protected().Verify("SendAsync", Times.Exactly(2), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }
}