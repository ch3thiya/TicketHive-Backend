using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Xunit;
using Catalog.Service.Clients;

namespace Catalog.Service.Tests.Api;

public class EventsAuthorizationApiTests : IClassFixture<CatalogApiFactory>
{
    private readonly CatalogApiFactory _factory;

    public EventsAuthorizationApiTests(CatalogApiFactory factory)
    {
        _factory = factory;
        _factory.OrganizerStatusClientMock.Reset();
        _factory.ResetRepositoryDefaults();
    }

    private static HttpRequestMessage CreateEventRequest(string? sub)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/events")
        {
            Content = JsonContent.Create(new
            {
                name = "Neon Summer Concert",
                description = "Live music event",
                category = "Concert",
                eventDate = (string?)null,
                eventTime = (string?)null,
                bannerUrl = ""
            })
        };

        if (sub is not null)
        {
            request.Headers.Add(TestAuthHandler.SubHeaderName, sub);
        }

        return request;
    }

    [Fact]
    public async Task CreateEvent_NoToken_ReturnsUnauthorized()
    {
        // Arrange
        var client = _factory.CreateClient();
        var request = CreateEventRequest(sub: null);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateEvent_AuthenticatedNonOrganizer_ReturnsForbidden()
    {
        // Arrange
        _factory.OrganizerStatusClientMock
            .Setup(c => c.GetOrganizerStatusAsync("customer-sub", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizerLookupResult(OrganizerLookupStatus.NotFound, null));
        var client = _factory.CreateClient();
        var request = CreateEventRequest(sub: "customer-sub");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateEvent_ApprovedOrganizer_ReturnsCreatedWithIdentityOrganizerId()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        _factory.OrganizerStatusClientMock
            .Setup(c => c.GetOrganizerStatusAsync("organizer-sub", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizerLookupResult(OrganizerLookupStatus.Active, organizerId));
        var client = _factory.CreateClient();
        var request = CreateEventRequest(sub: "organizer-sub");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(organizerId, body.GetProperty("organizerId").GetGuid());
    }

    [Fact]
    public async Task CreateEvent_IdentityUnreachable_ReturnsServiceUnavailable()
    {
        // Arrange
        _factory.OrganizerStatusClientMock
            .Setup(c => c.GetOrganizerStatusAsync("organizer-sub", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizerLookupResult(OrganizerLookupStatus.Unavailable, null));
        var client = _factory.CreateClient();
        var request = CreateEventRequest(sub: "organizer-sub");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task GetAllPublishedEvents_NoToken_ReturnsOk()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/catalog/events");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _factory.OrganizerStatusClientMock.Verify(
            c => c.GetOrganizerStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetMyEvents_NoToken_ReturnsUnauthorized()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/catalog/events/my-events");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetMyEvents_ApprovedOrganizer_ReturnsOk()
    {
        // Arrange
        _factory.OrganizerStatusClientMock
            .Setup(c => c.GetOrganizerStatusAsync("organizer-sub", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizerLookupResult(OrganizerLookupStatus.Active, Guid.NewGuid()));
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/events/my-events");
        request.Headers.Add(TestAuthHandler.SubHeaderName, "organizer-sub");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
