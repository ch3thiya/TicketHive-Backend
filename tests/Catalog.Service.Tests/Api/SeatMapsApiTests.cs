using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Catalog.Service.Clients;
using Catalog.Service.Db;
using Catalog.Service.Models;
using Moq;
using Xunit;

namespace Catalog.Service.Tests.Api;

// The real Catalog pipeline (routing, policies, controller, service) with repositories and Inventory faked.
public class SeatMapsApiTests : IClassFixture<CatalogApiFactory>
{
    private readonly CatalogApiFactory _factory;
    private readonly Guid _organizerId = Guid.NewGuid();
    private readonly Event _event;
    private readonly Show _show;
    private readonly TicketCategory _vip = new() { Id = Guid.NewGuid(), Name = "VIP", Capacity = 6, Price = 100 };
    private readonly TicketCategory _general = new() { Id = Guid.NewGuid(), Name = "General", Capacity = 12, Price = 40 };

    public SeatMapsApiTests(CatalogApiFactory factory)
    {
        _factory = factory;
        factory.OrganizerStatusClientMock.Reset();
        factory.SeatMapRepositoryMock.Reset();
        factory.InventorySeatClientMock.Reset();
        factory.InventoryClientMock.Reset();
        factory.ResetRepositoryDefaults();

        _event = new Event { Id = Guid.NewGuid(), OrganizerId = _organizerId, Name = "Gig", Status = "Draft" };
        _show = new Show { Id = Guid.NewGuid(), EventId = _event.Id, Status = "Active" };
        factory.EventRepositoryMock.Setup(r => r.GetShowByIdAsync(_show.Id)).ReturnsAsync(_show);
        factory.EventRepositoryMock.Setup(r => r.GetEventByIdAsync(_event.Id)).ReturnsAsync(_event);
        factory.EventRepositoryMock.Setup(r => r.GetTicketCategoriesByShowIdAsync(_show.Id)).ReturnsAsync(new List<TicketCategory> { _vip, _general });
        factory.OrganizerStatusClientMock
            .Setup(c => c.GetOrganizerStatusAsync("owner", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizerLookupResult(OrganizerLookupStatus.Active, _organizerId));
        factory.OrganizerStatusClientMock
            .Setup(c => c.GetOrganizerStatusAsync("other", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizerLookupResult(OrganizerLookupStatus.Active, Guid.NewGuid()));
        factory.OrganizerStatusClientMock
            .Setup(c => c.GetOrganizerStatusAsync("suspended-owner", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizerLookupResult(OrganizerLookupStatus.Suspended, _organizerId));
        factory.InventoryClientMock
            .Setup(c => c.InitializeShowStockAsync(It.IsAny<Guid>(), It.IsAny<InitializeShowStockRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private static HttpRequestMessage Send(HttpMethod method, string url, object? body = null, string? sub = null, string? role = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (sub is not null) request.Headers.Add(TestAuthHandler.SubHeaderName, sub);
        if (role is not null) request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
        return request;
    }

    private string RequestsUrl => $"/api/catalog/shows/{_show.Id}/seat-map-requests";

    private SeatMapRequest Pending(Guid? id = null) =>
        new(id ?? Guid.NewGuid(), _show.Id, _organizerId, "Stalls", SeatMapStatuses.Pending, null, DateTimeOffset.UtcNow, null, null);

    private static async Task<JsonElement> Body(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    // ---- organizer ----

    [Fact]
    public async Task Request_Anonymous_Returns401()
    {
        var response = await _factory.CreateClient().SendAsync(Send(HttpMethod.Post, RequestsUrl, new { description = "Stalls" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Request_OwningOrganizer_Returns201WithAPendingRequest()
    {
        _factory.SeatMapRepositoryMock.Setup(r => r.CreateRequestAsync(It.IsAny<SeatMapRequest>())).ReturnsAsync(CreateSeatMapRequestOutcome.Created);

        var response = await _factory.CreateClient().SendAsync(Send(HttpMethod.Post, RequestsUrl, new { description = "Stalls and balcony" }, "owner"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Body(response);
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.Equal(_organizerId, body.GetProperty("organizerId").GetGuid());
    }

    [Fact]
    public async Task Request_AnotherOrganizersShow_Returns403AndStoresNothing()
    {
        var response = await _factory.CreateClient().SendAsync(Send(HttpMethod.Post, RequestsUrl, new { description = "Stalls" }, "other"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _factory.SeatMapRepositoryMock.Verify(r => r.CreateRequestAsync(It.IsAny<SeatMapRequest>()), Times.Never);
    }

    [Fact]
    public async Task Request_SuspendedOrganizer_Returns403WithSuspendedCode()
    {
        var response = await _factory.CreateClient().SendAsync(Send(HttpMethod.Post, RequestsUrl, new { description = "Stalls" }, "suspended-owner"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OrganizerSuspended", (await Body(response)).GetProperty("code").GetString());
        _factory.SeatMapRepositoryMock.Verify(r => r.CreateRequestAsync(It.IsAny<SeatMapRequest>()), Times.Never);
    }

    [Fact]
    public async Task Request_EmptyDescription_Returns400()
    {
        var response = await _factory.CreateClient().SendAsync(Send(HttpMethod.Post, RequestsUrl, new { description = " " }, "owner"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Request_WhilePending_Returns409WithCode()
    {
        _factory.SeatMapRepositoryMock.Setup(r => r.CreateRequestAsync(It.IsAny<SeatMapRequest>())).ReturnsAsync(CreateSeatMapRequestOutcome.PendingExists);

        var response = await _factory.CreateClient().SendAsync(Send(HttpMethod.Post, RequestsUrl, new { description = "Stalls" }, "owner"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("RequestPending", (await Body(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ViewRequests_OwnerSeesStatusAndRejectionReason_EvenWhileSuspended()
    {
        var rejected = Pending() with { Status = SeatMapStatuses.Rejected, RejectionReason = "Venue plan missing" };
        _factory.SeatMapRepositoryMock.Setup(r => r.GetRequestsForShowAsync(_show.Id)).ReturnsAsync(new List<SeatMapRequest> { rejected });

        var response = await _factory.CreateClient().SendAsync(Send(HttpMethod.Get, RequestsUrl, sub: "suspended-owner"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var first = (await Body(response))[0];
        Assert.Equal("Rejected", first.GetProperty("status").GetString());
        Assert.Equal("Venue plan missing", first.GetProperty("rejectionReason").GetString());
    }

    [Fact]
    public async Task ViewRequests_AnotherOrganizer_Returns403()
    {
        var response = await _factory.CreateClient().SendAsync(Send(HttpMethod.Get, RequestsUrl, sub: "other"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _factory.SeatMapRepositoryMock.Verify(r => r.GetRequestsForShowAsync(It.IsAny<Guid>()), Times.Never);
    }

    // ---- admin authorization ----

    public static IEnumerable<object[]> AdminEndpoints()
    {
        var id = Guid.NewGuid();
        yield return new object[] { "GET", "/api/catalog/admin/seat-map-requests" };
        yield return new object[] { "GET", $"/api/catalog/admin/seat-map-requests/{id}" };
        yield return new object[] { "POST", $"/api/catalog/admin/seat-map-requests/{id}/layouts" };
        yield return new object[] { "POST", $"/api/catalog/admin/seat-layouts/{id}/enable" };
        yield return new object[] { "POST", $"/api/catalog/admin/seat-map-requests/{id}/reject" };
    }

    [Theory]
    [MemberData(nameof(AdminEndpoints))]
    public async Task AdminEndpoints_RequireAnAdmin(string method, string url)
    {
        var client = _factory.CreateClient();
        var body = new { sections = Array.Empty<object>(), reason = "x" };

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Send(new HttpMethod(method), url, body))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Send(new HttpMethod(method), url, body, "owner"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Send(new HttpMethod(method), url, body, "customer", "Customer"))).StatusCode);
        _factory.SeatMapRepositoryMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Admin_ListsAndFiltersRequests()
    {
        _factory.SeatMapRepositoryMock.Setup(r => r.ListRequestsAsync("Pending", It.IsAny<int>()))
            .ReturnsAsync(new List<SeatMapRequestSummary> { new(Pending(), "Gig", new DateOnly(2027, 1, 1), new TimeOnly(18, 0), 18) });

        var ok = await _factory.CreateClient().SendAsync(Send(HttpMethod.Get, "/api/catalog/admin/seat-map-requests?status=Pending", sub: "admin", role: "Admin"));
        var bad = await _factory.CreateClient().SendAsync(Send(HttpMethod.Get, "/api/catalog/admin/seat-map-requests?status=Nope", sub: "admin", role: "Admin"));

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(18, (await Body(ok))[0].GetProperty("ticketTotal").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Admin_CreatesAValidDraftLayout()
    {
        var request = Pending();
        _factory.SeatMapRepositoryMock.Setup(r => r.GetRequestAsync(request.Id)).ReturnsAsync(request);
        _factory.SeatMapRepositoryMock.Setup(r => r.CreateDraftAsync(request.Id, It.IsAny<SeatLayoutSpec>(), "admin", It.IsAny<DateTimeOffset>()))
            .ReturnsAsync((CreateDraftOutcome.Created, new SeatLayout(Guid.NewGuid(), _show.Id, request.Id, 1, "Draft", new SeatLayoutSpec(Array.Empty<SeatLayoutSection>()), 18, "admin", DateTimeOffset.UtcNow, null)));

        var response = await _factory.CreateClient().SendAsync(Send(HttpMethod.Post, $"/api/catalog/admin/seat-map-requests/{request.Id}/layouts",
            new { sections = new object[] { new { name = "Front", categoryId = _vip.Id, rows = 2, seatsPerRow = 3 }, new { name = "Hall", categoryId = _general.Id, rows = 3, seatsPerRow = 4 } } },
            "admin", "Admin"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Admin_InvalidLayout_Returns422WithEveryReason()
    {
        var request = Pending();
        _factory.SeatMapRepositoryMock.Setup(r => r.GetRequestAsync(request.Id)).ReturnsAsync(request);

        var response = await _factory.CreateClient().SendAsync(Send(HttpMethod.Post, $"/api/catalog/admin/seat-map-requests/{request.Id}/layouts",
            new { sections = new object[] { new { name = "Front", categoryId = Guid.Empty, rows = 2, seatsPerRow = 3 }, new { name = "front", categoryId = _general.Id, rows = 3, seatsPerRow = 4 } } },
            "admin", "Admin"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var codes = (await Body(response)).GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("code").GetString()).ToList();
        Assert.Contains("UnassignedSection", codes);
        Assert.Contains("DuplicateSection", codes);
        _factory.SeatMapRepositoryMock.Verify(r => r.CreateDraftAsync(It.IsAny<Guid>(), It.IsAny<SeatLayoutSpec>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    [Fact]
    public async Task Admin_CapacityMismatch_ReturnsAClearExplanation()
    {
        var request = Pending();
        _factory.SeatMapRepositoryMock.Setup(r => r.GetRequestAsync(request.Id)).ReturnsAsync(request);

        var response = await _factory.CreateClient().SendAsync(Send(HttpMethod.Post, $"/api/catalog/admin/seat-map-requests/{request.Id}/layouts",
            new { sections = new object[] { new { name = "Front", categoryId = _vip.Id, rows = 1, seatsPerRow = 3 }, new { name = "Hall", categoryId = _general.Id, rows = 3, seatsPerRow = 4 } } },
            "admin", "Admin"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var messages = (await Body(response)).GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("message").GetString()).ToList();
        Assert.Contains(messages, m => m!.Contains("VIP") && m.Contains("capacity 6") && m.Contains("3 seats"));
        Assert.Contains(messages, m => m!.Contains("ticket total"));
    }

    [Fact]
    public async Task Admin_Enable_SendsSeatsToInventoryAndReturns200()
    {
        var layout = new SeatLayout(Guid.NewGuid(), _show.Id, Guid.NewGuid(), 1, "Draft",
            new SeatLayoutSpec(new[] { new SeatLayoutSection("Front", _vip.Id, 2, 3), new SeatLayoutSection("Hall", _general.Id, 3, 4) }), 18, "admin", DateTimeOffset.UtcNow, null);
        _factory.SeatMapRepositoryMock.Setup(r => r.GetLayoutAsync(layout.Id)).ReturnsAsync(layout);
        _factory.SeatMapRepositoryMock.Setup(r => r.CompleteEnableAsync(layout.Id, "admin", It.IsAny<DateTimeOffset>())).ReturnsAsync(CompleteEnableOutcome.Enabled);
        _factory.InventorySeatClientMock.Setup(c => c.EnableSeatsAsync(_show.Id, 1, It.IsAny<IReadOnlyList<SeatPosition>>(), It.IsAny<CancellationToken>())).ReturnsAsync(EnableSeatsResult.Enabled);

        var response = await _factory.CreateClient().SendAsync(Send(HttpMethod.Post, $"/api/catalog/admin/seat-layouts/{layout.Id}/enable", sub: "admin", role: "Admin"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _factory.InventorySeatClientMock.Verify(c => c.EnableSeatsAsync(_show.Id, 1, It.Is<IReadOnlyList<SeatPosition>>(s => s.Count == 18), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(EnableSeatsResult.Locked, HttpStatusCode.Conflict, "LayoutLocked")]
    [InlineData(EnableSeatsResult.Unavailable, HttpStatusCode.ServiceUnavailable, null)]
    public async Task Admin_Enable_WhenInventoryRefuses_ReturnsTheMatchingProblem(EnableSeatsResult inventory, HttpStatusCode expected, string? code)
    {
        var layout = new SeatLayout(Guid.NewGuid(), _show.Id, Guid.NewGuid(), 1, "Draft",
            new SeatLayoutSpec(new[] { new SeatLayoutSection("Front", _vip.Id, 2, 3), new SeatLayoutSection("Hall", _general.Id, 3, 4) }), 18, "admin", DateTimeOffset.UtcNow, null);
        _factory.SeatMapRepositoryMock.Setup(r => r.GetLayoutAsync(layout.Id)).ReturnsAsync(layout);
        _factory.InventorySeatClientMock.Setup(c => c.EnableSeatsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<SeatPosition>>(), It.IsAny<CancellationToken>())).ReturnsAsync(inventory);

        var response = await _factory.CreateClient().SendAsync(Send(HttpMethod.Post, $"/api/catalog/admin/seat-layouts/{layout.Id}/enable", sub: "admin", role: "Admin"));

        Assert.Equal(expected, response.StatusCode);
        if (code is not null) Assert.Equal(code, (await Body(response)).GetProperty("code").GetString());
        if (expected == HttpStatusCode.ServiceUnavailable) Assert.True(response.Headers.Contains("Retry-After"));
        _factory.SeatMapRepositoryMock.Verify(r => r.CompleteEnableAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    [Fact]
    public async Task Admin_Reject_RequiresAReasonAndRecordsIt()
    {
        var request = Pending();
        _factory.SeatMapRepositoryMock.Setup(r => r.RejectAsync(request.Id, "Venue plan missing", "admin", It.IsAny<DateTimeOffset>())).ReturnsAsync(RejectOutcome.Rejected);
        _factory.SeatMapRepositoryMock.Setup(r => r.GetRequestAsync(request.Id)).ReturnsAsync(request with { Status = SeatMapStatuses.Rejected, RejectionReason = "Venue plan missing" });
        var url = $"/api/catalog/admin/seat-map-requests/{request.Id}/reject";

        var missing = await _factory.CreateClient().SendAsync(Send(HttpMethod.Post, url, new { reason = "" }, "admin", "Admin"));
        var ok = await _factory.CreateClient().SendAsync(Send(HttpMethod.Post, url, new { reason = "Venue plan missing" }, "admin", "Admin"));

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Venue plan missing", (await Body(ok)).GetProperty("rejectionReason").GetString());
    }
}
