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
using Catalog.Service.Models;

namespace Catalog.Service.Tests.Api;

/// <summary>
/// AC1/AC2 for Catalog: unexpected exceptions in EventsController's 500
/// paths must not leak exception text, while controlled 400/403/404
/// messages (already covered elsewhere, e.g. EventsAuthorizationApiTests)
/// must stay unchanged.
/// </summary>
public class EventsErrorResponseApiTests : IClassFixture<CatalogApiFactory>
{
    private const string SecretExceptionText = "connection string password=super-secret";

    private readonly CatalogApiFactory _factory;

    public EventsErrorResponseApiTests(CatalogApiFactory factory)
    {
        _factory = factory;
        _factory.OrganizerStatusClientMock.Reset();
        _factory.ResetRepositoryDefaults();
    }

    private static async Task AssertProblemDetailsWithoutExceptionText(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var rawBody = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SecretExceptionText, rawBody);

        var body = JsonDocument.Parse(rawBody).RootElement;
        Assert.True(body.TryGetProperty("detail", out var detail));
        Assert.False(string.IsNullOrWhiteSpace(detail.GetString()));
    }

    [Fact]
    public async Task CreateEvent_RepositoryThrows_ReturnsProblemDetailsWithoutExceptionText()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        _factory.OrganizerStatusClientMock
            .Setup(c => c.GetOrganizerStatusAsync("organizer-sub", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizerLookupResult(OrganizerLookupStatus.Active, organizerId));
        _factory.EventRepositoryMock
            .Setup(r => r.CreateEventAsync(It.IsAny<Event>()))
            .ThrowsAsync(new Exception(SecretExceptionText));

        var client = _factory.CreateClient();
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
        request.Headers.Add(TestAuthHandler.SubHeaderName, "organizer-sub");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        await AssertProblemDetailsWithoutExceptionText(response);
    }

    [Fact]
    public async Task CreateShow_RepositoryThrows_ReturnsProblemDetailsWithoutExceptionText()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        _factory.OrganizerStatusClientMock
            .Setup(c => c.GetOrganizerStatusAsync("organizer-sub", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizerLookupResult(OrganizerLookupStatus.Active, organizerId));
        _factory.EventRepositoryMock
            .Setup(r => r.GetEventByIdAsync(eventId))
            .ThrowsAsync(new Exception(SecretExceptionText));

        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/catalog/events/{eventId}/shows")
        {
            Content = JsonContent.Create(new
            {
                showDate = "2026-10-01",
                showTime = "19:30:00",
                categories = new[] { new { name = "General", price = 25.00m, capacity = 100 } }
            })
        };
        request.Headers.Add(TestAuthHandler.SubHeaderName, "organizer-sub");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        await AssertProblemDetailsWithoutExceptionText(response);
    }

    [Fact]
    public async Task GetAllPublishedEvents_RepositoryThrows_ReturnsProblemDetailsWithoutExceptionText()
    {
        // Arrange
        _factory.EventRepositoryMock
            .Setup(r => r.GetAllPublishedEventsAsync())
            .ThrowsAsync(new Exception(SecretExceptionText));

        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/catalog/events");

        // Assert
        await AssertProblemDetailsWithoutExceptionText(response);
    }

    [Fact]
    public async Task CreateEvent_MissingName_ReturnsBadRequestWithControlledMessageUnchanged()
    {
        // Arrange - AC2: the 500 fix must not touch this controlled 400 path.
        var organizerId = Guid.NewGuid();
        _factory.OrganizerStatusClientMock
            .Setup(c => c.GetOrganizerStatusAsync("organizer-sub", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizerLookupResult(OrganizerLookupStatus.Active, organizerId));

        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/events")
        {
            Content = JsonContent.Create(new
            {
                name = "",
                description = "Live music event",
                category = "Concert",
                eventDate = (string?)null,
                eventTime = (string?)null,
                bannerUrl = ""
            })
        };
        request.Headers.Add(TestAuthHandler.SubHeaderName, "organizer-sub");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("Event Name is required.", body.GetProperty("message").GetString());
    }
}
