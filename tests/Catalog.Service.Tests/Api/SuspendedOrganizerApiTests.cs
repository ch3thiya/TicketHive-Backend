using System;
using System.Collections.Generic;
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

public class SuspendedOrganizerApiTests : IClassFixture<CatalogApiFactory>
{
    private readonly CatalogApiFactory _factory;
    private readonly Guid _organizerId = Guid.NewGuid();

    public SuspendedOrganizerApiTests(CatalogApiFactory factory)
    {
        _factory = factory;
        _factory.OrganizerStatusClientMock.Reset();
        _factory.ResetRepositoryDefaults();
    }

    private void OrganizerIs(OrganizerLookupStatus status, string sub = "organizer-sub") =>
        _factory.OrganizerStatusClientMock
            .Setup(c => c.GetOrganizerStatusAsync(sub, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizerLookupResult(status, _organizerId));

    private static HttpRequestMessage Request(HttpMethod method, string url, string? sub = "organizer-sub", string? role = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(new { }) };
        if (sub is not null) request.Headers.Add(TestAuthHandler.SubHeaderName, sub);
        if (role is not null) request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
        return request;
    }

    public static IEnumerable<object[]> ManagementEndpoints()
    {
        var id = Guid.NewGuid();
        yield return new object[] { "POST", "/api/catalog/events" };
        yield return new object[] { "POST", $"/api/catalog/events/{id}/shows" };
        yield return new object[] { "PUT", $"/api/catalog/events/{id}" };
        yield return new object[] { "POST", $"/api/catalog/events/{id}/publish" };
        yield return new object[] { "POST", $"/api/catalog/events/{id}/cancel" };
        yield return new object[] { "DELETE", $"/api/catalog/events/{id}" };
        yield return new object[] { "PUT", $"/api/catalog/shows/{id}" };
        yield return new object[] { "POST", $"/api/catalog/shows/{id}/cancel" };
    }

    [Theory]
    [MemberData(nameof(ManagementEndpoints))]
    public async Task ManagementEndpoint_SuspendedOrganizer_ReturnsForbiddenProblemWithSuspendedCode(string method, string url)
    {
        // Arrange
        OrganizerIs(OrganizerLookupStatus.Suspended);
        var client = _factory.CreateClient();

        // Act
        var response = await client.SendAsync(Request(new HttpMethod(method), url));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("OrganizerSuspended", body.GetProperty("code").GetString());
        _factory.EventRepositoryMock.Verify(r => r.CreateEventAsync(It.IsAny<Event>()), Times.Never);
        _factory.EventRepositoryMock.Verify(r => r.UpdateEventStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        _factory.EventRepositoryMock.Verify(r => r.UpdateShowStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SuspendedResponse_DoesNotDiscloseAnySuspensionReason()
    {
        // Arrange
        OrganizerIs(OrganizerLookupStatus.Suspended);
        var client = _factory.CreateClient();

        // Act
        var response = await client.SendAsync(Request(HttpMethod.Post, "/api/catalog/events"));
        var text = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.DoesNotContain("reason", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fraud", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetMyEvents_SuspendedOrganizer_StaysReadableAndScopedToOwnEvents()
    {
        // Arrange
        OrganizerIs(OrganizerLookupStatus.Suspended);
        var client = _factory.CreateClient();

        // Act
        var response = await client.SendAsync(Request(HttpMethod.Get, "/api/catalog/events/my-events"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _factory.EventRepositoryMock.Verify(r => r.GetEventsByOrganizerIdAsync(_organizerId), Times.Once);
    }

    [Fact]
    public async Task OrganizerStatus_ReportsActiveOrSuspendedWithoutAReason()
    {
        // Arrange
        OrganizerIs(OrganizerLookupStatus.Active, "active-sub");
        OrganizerIs(OrganizerLookupStatus.Suspended, "suspended-sub");
        var client = _factory.CreateClient();

        // Act
        var active = await client.SendAsync(Request(HttpMethod.Get, "/api/catalog/organizer/status", "active-sub"));
        var suspended = await client.SendAsync(Request(HttpMethod.Get, "/api/catalog/organizer/status", "suspended-sub"));

        // Assert
        Assert.Equal("active", (await active.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        var suspendedBody = await suspended.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("suspended", suspendedBody.GetProperty("status").GetString());
        Assert.Single(suspendedBody.EnumerateObject());
    }

    [Fact]
    public async Task OrganizerStatus_NoTokenOrNonOrganizer_IsRefused()
    {
        // Arrange
        OrganizerIs(OrganizerLookupStatus.NotFound, "customer-sub");
        var client = _factory.CreateClient();

        // Act
        var anonymous = await client.SendAsync(Request(HttpMethod.Get, "/api/catalog/organizer/status", sub: null));
        var customer = await client.SendAsync(Request(HttpMethod.Get, "/api/catalog/organizer/status", "customer-sub"));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, customer.StatusCode);
    }

    [Fact]
    public async Task Management_AfterReinstatement_ResumesOnTheNextRequestWithoutRestart()
    {
        // Arrange
        var statuses = new Queue<OrganizerLookupStatus>([OrganizerLookupStatus.Suspended, OrganizerLookupStatus.Active, OrganizerLookupStatus.Suspended]);
        _factory.OrganizerStatusClientMock
            .Setup(c => c.GetOrganizerStatusAsync("organizer-sub", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new OrganizerLookupResult(statuses.Dequeue(), _organizerId));
        var client = _factory.CreateClient();

        // Act
        var whileSuspended = await client.SendAsync(Request(HttpMethod.Post, "/api/catalog/events"));
        var afterReinstate = await client.SendAsync(Request(HttpMethod.Post, "/api/catalog/events"));
        var suspendedAgain = await client.SendAsync(Request(HttpMethod.Post, "/api/catalog/events"));

        // Assert: every request asks Identity, so the first and third are refused and the second proceeds.
        Assert.Equal(HttpStatusCode.Forbidden, whileSuspended.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, afterReinstate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, suspendedAgain.StatusCode);
    }

    [Fact]
    public async Task AdminCancelShow_ForSuspendedOrganizersShow_StillWorks()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        _factory.EventRepositoryMock.Setup(r => r.GetShowByIdAsync(showId)).ReturnsAsync(new Show { Id = showId, EventId = eventId, Status = "Active" });
        _factory.EventRepositoryMock.Setup(r => r.GetEventByIdAsync(eventId)).ReturnsAsync(new Event { Id = eventId, OrganizerId = _organizerId, Status = "Published" });
        _factory.EventRepositoryMock.Setup(r => r.UpdateShowStatusAsync(showId, "Cancelled")).Returns(Task.CompletedTask);
        var client = _factory.CreateClient();

        // Act
        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/catalog/admin/shows/{showId}/cancel", "admin-sub", "admin"));

        // Assert
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        _factory.OrganizerStatusClientMock.Verify(c => c.GetOrganizerStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetEventById_SuspendedOrganizer_FlagsSalesSuspendedForCustomers()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        _factory.EventRepositoryMock.Setup(r => r.GetPublishedEventByIdAsync(eventId)).ReturnsAsync(new Event { Id = eventId, OrganizerId = _organizerId, Status = "Published", Name = "Gig" });
        _factory.OrganizerStatusClientMock
            .Setup(c => c.GetOrganizerStatusesAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, OrganizerLookupStatus> { [_organizerId] = OrganizerLookupStatus.Suspended });
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync($"/api/catalog/events/{eventId}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("salesSuspended").GetBoolean());
        Assert.Equal("Published", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetEventById_ActiveOrganizer_IsNotFlagged()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        _factory.EventRepositoryMock.Setup(r => r.GetPublishedEventByIdAsync(eventId)).ReturnsAsync(new Event { Id = eventId, OrganizerId = _organizerId, Status = "Published", Name = "Gig" });
        _factory.OrganizerStatusClientMock
            .Setup(c => c.GetOrganizerStatusesAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, OrganizerLookupStatus> { [_organizerId] = OrganizerLookupStatus.Active });
        var client = _factory.CreateClient();

        // Act
        var body = await (await client.GetAsync($"/api/catalog/events/{eventId}")).Content.ReadFromJsonAsync<JsonElement>();

        // Assert
        Assert.False(body.GetProperty("salesSuspended").GetBoolean());
    }

    [Fact]
    public async Task GetEventById_IdentityOutage_DoesNotBlockTheCustomerPage()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        _factory.EventRepositoryMock.Setup(r => r.GetPublishedEventByIdAsync(eventId)).ReturnsAsync(new Event { Id = eventId, OrganizerId = _organizerId, Status = "Published", Name = "Gig" });
        _factory.OrganizerStatusClientMock
            .Setup(c => c.GetOrganizerStatusesAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, OrganizerLookupStatus> { [_organizerId] = OrganizerLookupStatus.Unavailable });
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync($"/api/catalog/events/{eventId}");

        // Assert: display falls back to "not flagged"; the hold-time check stays authoritative.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}