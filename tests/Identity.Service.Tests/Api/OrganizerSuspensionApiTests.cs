using System.Net;
using System.Net.Http.Json;
using Moq;
using Xunit;
using Identity.Service.Controllers;
using Identity.Service.Db;
using Identity.Service.Models;

namespace Identity.Service.Tests.Api;

public class OrganizerSuspensionApiTests : IClassFixture<AdminApiFactory>
{
    private readonly AdminApiFactory _factory;

    public OrganizerSuspensionApiTests(AdminApiFactory factory)
    {
        _factory = factory;
        _factory.AccountRepositoryMock.Reset();
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? role, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (role is not null)
        {
            request.Headers.Add(TestAuthHandler.SubHeaderName, "caller-sub");
            request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private void SetupApply(OrganizerStatusChangeOutcome outcome, string? status) =>
        _factory.AccountRepositoryMock
            .Setup(r => r.ApplyOrganizerStatusChangeAsync(It.IsAny<Guid>(), It.IsAny<OrganizerStatusAction>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(new OrganizerStatusChangeResult(outcome, status));

    [Theory]
    [InlineData("suspend")]
    [InlineData("reinstate")]
    public async Task Command_NoToken_ReturnsUnauthorized(string command)
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/identity/organizer-requests/organizers/{Guid.NewGuid()}/{command}", null, new { reason = "x" }));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Customer", "suspend")]
    [InlineData("Organizer", "suspend")]
    [InlineData("Customer", "reinstate")]
    [InlineData("Organizer", "reinstate")]
    public async Task Command_NonAdmin_ReturnsForbiddenWithoutWriting(string role, string command)
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/identity/organizer-requests/organizers/{Guid.NewGuid()}/{command}", role, new { reason = "x" }));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _factory.AccountRepositoryMock.Verify(r => r.ApplyOrganizerStatusChangeAsync(
            It.IsAny<Guid>(), It.IsAny<OrganizerStatusAction>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    [Fact]
    public async Task Suspend_NonAdminOnHistory_ReturnsForbidden()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/identity/organizer-requests/organizers/{Guid.NewGuid()}/status-history", "Organizer"));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Suspend_AdminWithoutReason_ReturnsProblemDetails400()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/identity/organizer-requests/organizers/{Guid.NewGuid()}/suspend", "Admin", new { reason = "  " }));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Suspend_AdminWithReason_ReturnsSuspendedAndRecordsActor()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        SetupApply(OrganizerStatusChangeOutcome.Changed, "suspended");
        var client = _factory.CreateClient();

        // Act
        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/identity/organizer-requests/organizers/{organizerId}/suspend", "Admin", new { reason = "Fraud" }));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OrganizerStatusResponse>();
        Assert.Equal(new OrganizerStatusResponse(organizerId, "suspended", false), body);
        _factory.AccountRepositoryMock.Verify(r => r.ApplyOrganizerStatusChangeAsync(
            organizerId, OrganizerStatusAction.Suspend, "caller-sub", "Fraud", It.IsAny<DateTimeOffset>()), Times.Once);
    }

    [Fact]
    public async Task Suspend_RepeatedRequest_ReturnsOkMarkedAsRepeat()
    {
        // Arrange
        SetupApply(OrganizerStatusChangeOutcome.Unchanged, "suspended");
        var client = _factory.CreateClient();

        // Act
        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/identity/organizer-requests/organizers/{Guid.NewGuid()}/suspend", "Admin", new { reason = "Fraud" }));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OrganizerStatusResponse>();
        Assert.True(body!.Repeated);
    }

    [Fact]
    public async Task Suspend_UnknownOrganizer_ReturnsNotFound()
    {
        // Arrange
        SetupApply(OrganizerStatusChangeOutcome.NotFound, null);
        var client = _factory.CreateClient();

        // Act
        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/identity/organizer-requests/organizers/{Guid.NewGuid()}/suspend", "Admin", new { reason = "Fraud" }));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Suspend_PendingOrganizer_ReturnsConflict()
    {
        // Arrange
        SetupApply(OrganizerStatusChangeOutcome.InvalidTransition, null);
        var client = _factory.CreateClient();

        // Act
        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/identity/organizer-requests/organizers/{Guid.NewGuid()}/suspend", "Admin", new { reason = "Fraud" }));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Reinstate_AdminWithoutBody_ReinstatesOrganizer()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        SetupApply(OrganizerStatusChangeOutcome.Changed, "approved");
        var client = _factory.CreateClient();

        // Act
        var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/identity/organizer-requests/organizers/{organizerId}/reinstate", "Admin"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OrganizerStatusResponse>();
        Assert.Equal("approved", body!.Status);
    }

    [Fact]
    public async Task StatusHistory_AdminForOrganizer_ReturnsEntries()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        _factory.AccountRepositoryMock.Setup(r => r.GetUserAccountByIdAsync(organizerId)).ReturnsAsync(new UserAccount { Id = organizerId, Role = "Organizer" });
        _factory.AccountRepositoryMock.Setup(r => r.GetOrganizerStatusHistoryAsync(organizerId)).ReturnsAsync(
            [new OrganizerStatusAuditEntry(Guid.NewGuid(), organizerId, "Suspended", "Fraud", "admin-1", DateTimeOffset.UtcNow)]);
        var client = _factory.CreateClient();

        // Act
        var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/identity/organizer-requests/organizers/{organizerId}/status-history", "Admin"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entries = await response.Content.ReadFromJsonAsync<List<OrganizerStatusAuditEntry>>();
        Assert.Single(entries!);
        Assert.Equal("admin-1", entries![0].ActorSub);
    }
}