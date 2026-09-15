using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Xunit;
using Catalog.Service.Clients;
using Catalog.Service.Models;

namespace Catalog.Service.Tests.Api;

/// <summary>
/// Proves that refused status transitions surface through the real HTTP
/// pipeline as 409 with a ProblemDetails body, not 400 or 500.
/// </summary>
public class EventStatusTransitionApiTests : IClassFixture<CatalogApiFactory>
{
    private readonly CatalogApiFactory _factory;

    public EventStatusTransitionApiTests(CatalogApiFactory factory)
    {
        _factory = factory;
        _factory.OrganizerStatusClientMock.Reset();
        _factory.ResetRepositoryDefaults();
    }

    private Guid AuthorizeAsOrganizer(string sub)
    {
        var organizerId = Guid.NewGuid();
        _factory.OrganizerStatusClientMock
            .Setup(c => c.GetOrganizerStatusAsync(sub, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizerLookupResult(OrganizerLookupStatus.Active, organizerId));
        return organizerId;
    }

    private static HttpRequestMessage WithAuth(HttpRequestMessage request, string sub)
    {
        request.Headers.Add(TestAuthHandler.SubHeaderName, sub);
        return request;
    }

    private static async Task AssertConflictProblemDetails(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(409, body.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task PublishEvent_CancelledEvent_ReturnsConflictProblemDetails()
    {
        // Arrange
        const string sub = "organizer-publish-cancelled";
        var organizerId = AuthorizeAsOrganizer(sub);
        var eventId = Guid.NewGuid();

        _factory.EventRepositoryMock.Setup(r => r.GetEventByIdAsync(eventId))
            .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId, Status = "Cancelled" });

        var client = _factory.CreateClient();
        var request = WithAuth(new HttpRequestMessage(HttpMethod.Post, $"/api/catalog/events/{eventId}/publish"), sub);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        await AssertConflictProblemDetails(response);
        _factory.EventRepositoryMock.Verify(r => r.UpdateEventStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task PublishEvent_InventoryUnavailable_ReturnsServiceUnavailableAndEventStaysDraft()
    {
        // Arrange
        const string sub = "organizer-publish-inventory-down";
        var organizerId = AuthorizeAsOrganizer(sub);
        var eventId = Guid.NewGuid();
        var showId = Guid.NewGuid();

        _factory.EventRepositoryMock.Setup(r => r.GetEventByIdAsync(eventId))
            .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId, Status = "Draft" });
        _factory.EventRepositoryMock.Setup(r => r.GetShowsByEventIdAsync(eventId))
            .ReturnsAsync(new List<Show> { new Show { Id = showId, EventId = eventId, Status = "Active" } });
        _factory.EventRepositoryMock.Setup(r => r.GetTicketCategoriesByShowIdAsync(showId))
            .ReturnsAsync(new List<TicketCategory> { new TicketCategory { Id = Guid.NewGuid(), ShowId = showId, Name = "GA", Price = 20, Capacity = 50 } });
        _factory.InventoryClientMock
            .Setup(c => c.InitializeShowStockAsync(showId, It.IsAny<InitializeShowStockRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InventoryUnavailableException("Inventory is unavailable."));

        var client = _factory.CreateClient();
        var request = WithAuth(new HttpRequestMessage(HttpMethod.Post, $"/api/catalog/events/{eventId}/publish"), sub);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        _factory.EventRepositoryMock.Verify(r => r.UpdateEventStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CancelEvent_AlreadyCancelledEvent_ReturnsConflictProblemDetails()
    {
        // Arrange
        const string sub = "organizer-cancel-cancelled";
        var organizerId = AuthorizeAsOrganizer(sub);
        var eventId = Guid.NewGuid();

        _factory.EventRepositoryMock.Setup(r => r.GetEventByIdAsync(eventId))
            .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId, Status = "Cancelled" });

        var client = _factory.CreateClient();
        var request = WithAuth(new HttpRequestMessage(HttpMethod.Post, $"/api/catalog/events/{eventId}/cancel"), sub);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        await AssertConflictProblemDetails(response);
        _factory.EventRepositoryMock.Verify(r => r.UpdateEventStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateEvent_CancelledEvent_ReturnsConflictProblemDetails()
    {
        // Arrange
        const string sub = "organizer-update-cancelled";
        var organizerId = AuthorizeAsOrganizer(sub);
        var eventId = Guid.NewGuid();

        _factory.EventRepositoryMock.Setup(r => r.GetEventByIdAsync(eventId))
            .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId, Name = "Old Name", Status = "Cancelled" });

        var client = _factory.CreateClient();
        var request = WithAuth(new HttpRequestMessage(HttpMethod.Put, $"/api/catalog/events/{eventId}"), sub);
        request.Content = JsonContent.Create(new
        {
            name = "Updated Name",
            description = "",
            category = "",
            eventDate = (string?)null,
            eventTime = (string?)null,
            bannerUrl = ""
        });

        // Act
        var response = await client.SendAsync(request);

        // Assert
        await AssertConflictProblemDetails(response);
        _factory.EventRepositoryMock.Verify(r => r.UpdateEventAsync(It.IsAny<Event>()), Times.Never);
    }

    [Fact]
    public async Task CancelShow_AlreadyCancelledShow_ReturnsConflictProblemDetails()
    {
        // Arrange
        const string sub = "organizer-cancel-cancelled-show";
        var organizerId = AuthorizeAsOrganizer(sub);
        var eventId = Guid.NewGuid();
        var showId = Guid.NewGuid();

        _factory.EventRepositoryMock.Setup(r => r.GetShowByIdAsync(showId))
            .ReturnsAsync(new Show { Id = showId, EventId = eventId, Status = "Cancelled" });
        _factory.EventRepositoryMock.Setup(r => r.GetEventByIdAsync(eventId))
            .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId, Status = "Published" });

        var client = _factory.CreateClient();
        var request = WithAuth(new HttpRequestMessage(HttpMethod.Post, $"/api/catalog/shows/{showId}/cancel"), sub);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        await AssertConflictProblemDetails(response);
        _factory.EventRepositoryMock.Verify(r => r.UpdateShowStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateShow_CancelledShow_ReturnsConflictProblemDetails()
    {
        // Arrange
        const string sub = "organizer-update-cancelled-show";
        var organizerId = AuthorizeAsOrganizer(sub);
        var eventId = Guid.NewGuid();
        var showId = Guid.NewGuid();

        _factory.EventRepositoryMock.Setup(r => r.GetShowByIdAsync(showId))
            .ReturnsAsync(new Show { Id = showId, EventId = eventId, Status = "Cancelled" });
        _factory.EventRepositoryMock.Setup(r => r.GetEventByIdAsync(eventId))
            .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId });

        var client = _factory.CreateClient();
        var request = WithAuth(new HttpRequestMessage(HttpMethod.Put, $"/api/catalog/shows/{showId}"), sub);
        request.Content = JsonContent.Create(new
        {
            showDate = "2026-09-02",
            showTime = "20:00:00"
        });

        // Act
        var response = await client.SendAsync(request);

        // Assert
        await AssertConflictProblemDetails(response);
        _factory.EventRepositoryMock.Verify(r => r.UpdateShowAsync(It.IsAny<Show>()), Times.Never);
    }
}
