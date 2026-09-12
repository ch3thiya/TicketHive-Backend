using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Xunit;
using Catalog.Service.Clients;
using Catalog.Service.Db;
using Catalog.Service.Models;

namespace Catalog.Service.Tests.Api;

/// <summary>
/// Proves that an id rejected by SaveTicketCategoriesAsync (foreign or
/// retired category id) surfaces through the real HTTP pipeline as 400,
/// not 500 — the UpdateShow action's ArgumentException catch is what makes
/// this work; SQL behaviour itself is covered by the repository's
/// Testcontainers integration tests.
/// </summary>
public class TicketCategoryUpdateApiTests : IClassFixture<CatalogApiFactory>
{
    private readonly CatalogApiFactory _factory;

    public TicketCategoryUpdateApiTests(CatalogApiFactory factory)
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

    [Fact]
    public async Task UpdateShow_CategoryIdRejectedByRepository_ReturnsBadRequest()
    {
        // Arrange
        const string sub = "organizer-update-show-bad-category";
        var organizerId = AuthorizeAsOrganizer(sub);
        var eventId = Guid.NewGuid();
        var showId = Guid.NewGuid();
        var rejectedCategoryId = Guid.NewGuid();

        _factory.EventRepositoryMock.Setup(r => r.GetShowByIdAsync(showId))
            .ReturnsAsync(new Show { Id = showId, EventId = eventId, Status = "Active" });
        _factory.EventRepositoryMock.Setup(r => r.GetEventByIdAsync(eventId))
            .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId });
        _factory.EventRepositoryMock.Setup(r => r.SaveTicketCategoriesAsync(showId, It.IsAny<System.Collections.Generic.List<TicketCategory>>()))
            .ThrowsAsync(new ArgumentException($"Ticket category '{rejectedCategoryId}' does not belong to this show."));

        var client = _factory.CreateClient();
        var request = WithAuth(new HttpRequestMessage(HttpMethod.Put, $"/api/catalog/shows/{showId}"), sub);
        request.Content = JsonContent.Create(new
        {
            showDate = "2026-09-02",
            showTime = "20:00:00",
            categories = new[]
            {
                new { id = rejectedCategoryId, name = "VIP", price = 100.0m, capacity = 20 }
            }
        });

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        _factory.EventRepositoryMock.Verify(r => r.UpdateShowAsync(It.IsAny<Show>()), Times.Once);
    }
}
