using System.Net;
using Catalog.Service.Clients;
using Catalog.Service.Models;
using Moq;
using Xunit;

namespace Catalog.Service.Tests.Api;

public class CancellationAuthorizationApiTests : IClassFixture<CatalogApiFactory>
{
    private readonly CatalogApiFactory factory;
    public CancellationAuthorizationApiTests(CatalogApiFactory factory)
    {
        this.factory = factory;
        factory.OrganizerStatusClientMock.Reset();
        factory.ResetRepositoryDefaults();
    }

    private static HttpRequestMessage Request(HttpMethod method, string uri, string? sub = null, string? role = null, string? scope = null, string? aut = null)
    {
        var request = new HttpRequestMessage(method, uri);
        if (sub is not null) request.Headers.Add(TestAuthHandler.SubHeaderName, sub);
        if (role is not null) request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
        if (scope is not null) request.Headers.Add(TestAuthHandler.ScopeHeaderName, scope);
        if (aut is not null) request.Headers.Add(TestAuthHandler.AuthenticationTypeHeaderName, aut);
        return request;
    }

    [Fact]
    public async Task Organizer_cancellation_requires_active_organizer_and_owner()
    {
        var showId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        factory.EventRepositoryMock.Setup(r => r.GetShowByIdAsync(showId)).ReturnsAsync(new Show { Id = showId, EventId = eventId, Status = "Active" });
        factory.EventRepositoryMock.Setup(r => r.GetEventByIdAsync(eventId)).ReturnsAsync(new Event { Id = eventId, OrganizerId = ownerId, Status = "Published" });
        factory.EventRepositoryMock.Setup(r => r.UpdateShowStatusAsync(showId, "Cancelled")).Returns(Task.CompletedTask);
        factory.OrganizerStatusClientMock.Setup(c => c.GetOrganizerStatusAsync("owner", It.IsAny<CancellationToken>())).ReturnsAsync(new OrganizerLookupResult(OrganizerLookupStatus.Active, ownerId));
        factory.OrganizerStatusClientMock.Setup(c => c.GetOrganizerStatusAsync("other", It.IsAny<CancellationToken>())).ReturnsAsync(new OrganizerLookupResult(OrganizerLookupStatus.Active, Guid.NewGuid()));
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Request(HttpMethod.Post, $"/api/catalog/shows/{showId}/cancel"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Post, $"/api/catalog/shows/{showId}/cancel", "other"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(Request(HttpMethod.Post, $"/api/catalog/shows/{showId}/cancel", "owner"))).StatusCode);
    }

    [Fact]
    public async Task Admin_cancellation_requires_admin_role()
    {
        var eventId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        factory.EventRepositoryMock.Setup(r => r.GetEventByIdAsync(eventId)).ReturnsAsync(new Event { Id = eventId, OrganizerId = ownerId, Status = "Published" });
        factory.EventRepositoryMock.Setup(r => r.UpdateEventStatusAsync(eventId, "Cancelled")).Returns(Task.CompletedTask);
        factory.EventRepositoryMock.Setup(r => r.GetShowsByEventIdAsync(eventId)).ReturnsAsync(new List<Show>());
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/catalog/admin/events/{eventId}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Post, $"/api/catalog/admin/events/{eventId}/cancel", "customer"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(Request(HttpMethod.Post, $"/api/catalog/admin/events/{eventId}/cancel", "admin", "admin"))).StatusCode);
    }

    [Fact]
    public async Task Internal_rules_requires_application_token_and_catalog_scope()
    {
        var showId = Guid.NewGuid();
        factory.EventRepositoryMock.Setup(r => r.GetShowByIdAsync(showId)).ReturnsAsync(new Show { Id = showId, EventId = Guid.NewGuid(), Status = "Published", ShowDate = new DateOnly(2027, 1, 1), ShowTime = new TimeOnly(18, 0) });
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/internal/catalog/cancellations/shows/{showId}/rules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Get, $"/internal/catalog/cancellations/shows/{showId}/rules", "user", scope: "catalog:read", aut: "USER"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Get, $"/internal/catalog/cancellations/shows/{showId}/rules", "app", scope: "wrong", aut: "APPLICATION"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Get, $"/internal/catalog/cancellations/shows/{showId}/rules", "app", scope: "catalog:read", aut: "APPLICATION"))).StatusCode);
    }
}
