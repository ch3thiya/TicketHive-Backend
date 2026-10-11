using Catalog.Service.Clients;
using Catalog.Service.Db;
using Catalog.Service.Models;
using Catalog.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Catalog.Service.Tests;

public class SeatMapServiceTests
{
    private readonly Mock<ISeatMapRepository> _seatMaps = new();
    private readonly Mock<IEventRepository> _events = new();
    private readonly Mock<IInventoryClient> _inventory = new();
    private readonly Mock<IInventorySeatClient> _inventorySeats = new();
    private readonly SeatMapService _service;

    private readonly Guid _organizer = Guid.NewGuid();
    private readonly Event _event;
    private readonly Show _show;
    private readonly TicketCategory _vip = new() { Id = Guid.NewGuid(), Name = "VIP", Capacity = 6, Price = 100 };
    private readonly TicketCategory _general = new() { Id = Guid.NewGuid(), Name = "General", Capacity = 12, Price = 40 };

    public SeatMapServiceTests()
    {
        _event = new Event { Id = Guid.NewGuid(), OrganizerId = _organizer, Name = "Gig", Status = "Draft" };
        _show = new Show { Id = Guid.NewGuid(), EventId = _event.Id, Status = "Active" };
        _events.Setup(e => e.GetShowByIdAsync(_show.Id)).ReturnsAsync(_show);
        _events.Setup(e => e.GetEventByIdAsync(_event.Id)).ReturnsAsync(_event);
        _events.Setup(e => e.GetTicketCategoriesByShowIdAsync(_show.Id)).ReturnsAsync(new List<TicketCategory> { _vip, _general });
        _inventory.Setup(i => i.InitializeShowStockAsync(It.IsAny<Guid>(), It.IsAny<InitializeShowStockRequest>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _inventorySeats.Setup(i => i.EnableSeatsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<SeatPosition>>(), It.IsAny<CancellationToken>())).ReturnsAsync(EnableSeatsResult.Enabled);
        _inventorySeats.Setup(i => i.GetLockAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new SeatLockInfo(false, false, null));
        _service = new SeatMapService(_seatMaps.Object, _events.Object, _inventory.Object, _inventorySeats.Object,
            Options.Create(new PublishDefaultsOptions()), new FakeTimeProvider(), NullLogger<SeatMapService>.Instance);
    }

    private SeatLayoutSpec ValidSpec() => new(new[]
    {
        new SeatLayoutSection("Front", _vip.Id, 2, 3),
        new SeatLayoutSection("Hall", _general.Id, 3, 4)
    });

    private SeatLayout Layout(string status = SeatLayoutStatuses.Draft, SeatLayoutSpec? spec = null) =>
        new(Guid.NewGuid(), _show.Id, Guid.NewGuid(), 1, status, spec ?? ValidSpec(), 18, "admin", DateTimeOffset.UtcNow, null);

    private void Returns(SeatLayout layout) => _seatMaps.Setup(r => r.GetLayoutAsync(layout.Id)).ReturnsAsync(layout);

    // ---- organizer request ----

    [Fact]
    public async Task Request_OwnerWithDescription_IsStoredAsPending()
    {
        _seatMaps.Setup(r => r.CreateRequestAsync(It.IsAny<SeatMapRequest>())).ReturnsAsync(CreateSeatMapRequestOutcome.Created);

        var result = await _service.RequestAsync(_organizer, _show.Id, "  Stalls and a balcony  ");

        Assert.Equal(SeatMapStatus.Ok, result.Status);
        Assert.Equal("Stalls and a balcony", result.Value!.Description);
        Assert.Equal(SeatMapStatuses.Pending, result.Value.Status);
        Assert.Equal(_organizer, result.Value.OrganizerId);
    }

    [Fact]
    public async Task Request_ByAnotherOrganizer_IsForbiddenAndNothingIsStored()
    {
        var result = await _service.RequestAsync(Guid.NewGuid(), _show.Id, "Stalls");

        Assert.Equal(SeatMapStatus.Forbidden, result.Status);
        _seatMaps.Verify(r => r.CreateRequestAsync(It.IsAny<SeatMapRequest>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Request_WithoutDescription_IsInvalid(string? description) =>
        Assert.Equal(SeatMapStatus.Invalid, (await _service.RequestAsync(_organizer, _show.Id, description)).Status);

    [Fact]
    public async Task Request_TooLongDescription_IsInvalid() =>
        Assert.Equal(SeatMapStatus.Invalid, (await _service.RequestAsync(_organizer, _show.Id, new string('x', 1001))).Status);

    [Fact]
    public async Task Request_UnknownShow_IsNotFound() =>
        Assert.Equal(SeatMapStatus.NotFound, (await _service.RequestAsync(_organizer, Guid.NewGuid(), "Stalls")).Status);

    [Fact]
    public async Task Request_CancelledShow_IsConflict()
    {
        _show.Status = "Cancelled";

        Assert.Equal(SeatMapStatus.Conflict, (await _service.RequestAsync(_organizer, _show.Id, "Stalls")).Status);
    }

    [Theory]
    [InlineData(CreateSeatMapRequestOutcome.AlreadySeated, "AlreadySeated")]
    [InlineData(CreateSeatMapRequestOutcome.PendingExists, "RequestPending")]
    public async Task Request_WhenSeatedOrPending_IsConflictWithCode(CreateSeatMapRequestOutcome outcome, string code)
    {
        _seatMaps.Setup(r => r.CreateRequestAsync(It.IsAny<SeatMapRequest>())).ReturnsAsync(outcome);

        var result = await _service.RequestAsync(_organizer, _show.Id, "Stalls");

        Assert.Equal(SeatMapStatus.Conflict, result.Status);
        Assert.Equal(code, result.Code);
    }

    [Fact]
    public async Task GetForOrganizer_OnlyForTheOwner()
    {
        _seatMaps.Setup(r => r.GetRequestsForShowAsync(_show.Id)).ReturnsAsync(new List<SeatMapRequest>());

        Assert.Equal(SeatMapStatus.Ok, (await _service.GetForOrganizerAsync(_organizer, _show.Id)).Status);
        Assert.Equal(SeatMapStatus.Forbidden, (await _service.GetForOrganizerAsync(Guid.NewGuid(), _show.Id)).Status);
    }

    // ---- admin: draft ----

    [Fact]
    public async Task CreateDraft_ValidLayout_IsStored()
    {
        var request = new SeatMapRequest(Guid.NewGuid(), _show.Id, _organizer, "d", SeatMapStatuses.Pending, null, DateTimeOffset.UtcNow, null, null);
        _seatMaps.Setup(r => r.GetRequestAsync(request.Id)).ReturnsAsync(request);
        _seatMaps.Setup(r => r.CreateDraftAsync(request.Id, It.IsAny<SeatLayoutSpec>(), "admin", It.IsAny<DateTimeOffset>()))
            .ReturnsAsync((CreateDraftOutcome.Created, Layout()));

        var result = await _service.CreateDraftAsync(request.Id, ValidSpec(), "admin");

        Assert.Equal(SeatMapStatus.Ok, result.Status);
    }

    [Fact]
    public async Task CreateDraft_InvalidLayout_ReturnsAllErrorsAndStoresNothing()
    {
        var request = new SeatMapRequest(Guid.NewGuid(), _show.Id, _organizer, "d", SeatMapStatuses.Pending, null, DateTimeOffset.UtcNow, null, null);
        _seatMaps.Setup(r => r.GetRequestAsync(request.Id)).ReturnsAsync(request);
        var spec = new SeatLayoutSpec(new[] { new SeatLayoutSection("Front", Guid.Empty, 2, 3), new SeatLayoutSection("Hall", _general.Id, 3, 4) });

        var result = await _service.CreateDraftAsync(request.Id, spec, "admin");

        Assert.Equal(SeatMapStatus.Invalid, result.Status);
        Assert.Contains(result.Errors!, e => e.Code == "UnassignedSection");
        _seatMaps.Verify(r => r.CreateDraftAsync(It.IsAny<Guid>(), It.IsAny<SeatLayoutSpec>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    [Fact]
    public async Task CreateDraft_ForARejectedRequest_IsConflict()
    {
        var request = new SeatMapRequest(Guid.NewGuid(), _show.Id, _organizer, "d", SeatMapStatuses.Rejected, "no", DateTimeOffset.UtcNow, null, null);
        _seatMaps.Setup(r => r.GetRequestAsync(request.Id)).ReturnsAsync(request);

        Assert.Equal(SeatMapStatus.Conflict, (await _service.CreateDraftAsync(request.Id, ValidSpec(), "admin")).Status);
    }

    [Theory]
    [InlineData(true, SeatMapStatus.Conflict, "LayoutLocked")]
    [InlineData(null, SeatMapStatus.Unavailable, null)]
    public async Task CreateDraft_ForASeatedShow_ChecksTheLockAndFailsClosed(bool? locked, SeatMapStatus expected, string? code)
    {
        _show.SeatingMode = SeatingModes.Seated;
        var request = new SeatMapRequest(Guid.NewGuid(), _show.Id, _organizer, "d", SeatMapStatuses.Completed, null, DateTimeOffset.UtcNow, null, null);
        _seatMaps.Setup(r => r.GetRequestAsync(request.Id)).ReturnsAsync(request);
        _inventorySeats.Setup(i => i.GetLockAsync(_show.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(locked is null ? null : new SeatLockInfo(true, locked.Value, 1));

        var result = await _service.CreateDraftAsync(request.Id, ValidSpec(), "admin");

        Assert.Equal(expected, result.Status);
        Assert.Equal(code, result.Code);
        _seatMaps.Verify(r => r.CreateDraftAsync(It.IsAny<Guid>(), It.IsAny<SeatLayoutSpec>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    // ---- admin: enable ----

    [Fact]
    public async Task Enable_InitialisesStockThenSeatsThenCommits_InThatOrder()
    {
        var layout = Layout();
        Returns(layout);
        var order = new List<string>();
        _inventory.Setup(i => i.InitializeShowStockAsync(_show.Id, It.IsAny<InitializeShowStockRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("stock")).Returns(Task.CompletedTask);
        _inventorySeats.Setup(i => i.EnableSeatsAsync(_show.Id, 1, It.IsAny<IReadOnlyList<SeatPosition>>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("seats")).ReturnsAsync(EnableSeatsResult.Enabled);
        _seatMaps.Setup(r => r.CompleteEnableAsync(layout.Id, "admin", It.IsAny<DateTimeOffset>()))
            .Callback(() => order.Add("commit")).ReturnsAsync(CompleteEnableOutcome.Enabled);

        var result = await _service.EnableAsync(layout.Id, "admin");

        Assert.Equal(SeatMapStatus.Ok, result.Status);
        Assert.Equal(new[] { "stock", "seats", "commit" }, order);
        _inventorySeats.Verify(i => i.EnableSeatsAsync(_show.Id, 1, It.Is<IReadOnlyList<SeatPosition>>(s => s.Count == 18), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Enable_AlreadyEnabledLayout_IsAnIdempotentSuccessWithoutCallingInventory()
    {
        var layout = Layout(SeatLayoutStatuses.Enabled);
        Returns(layout);

        var result = await _service.EnableAsync(layout.Id, "admin");

        Assert.Equal(SeatMapStatus.Ok, result.Status);
        _inventorySeats.Verify(i => i.EnableSeatsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<SeatPosition>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Enable_SupersededLayout_IsConflict()
    {
        var layout = Layout(SeatLayoutStatuses.Superseded);
        Returns(layout);

        Assert.Equal(SeatMapStatus.Conflict, (await _service.EnableAsync(layout.Id, "admin")).Status);
    }

    [Fact]
    public async Task Enable_WhenCategoriesChangedSinceTheDraft_IsInvalidAndNothingIsSent()
    {
        var layout = Layout();
        Returns(layout);
        _vip.Capacity = 8;

        var result = await _service.EnableAsync(layout.Id, "admin");

        Assert.Equal(SeatMapStatus.Invalid, result.Status);
        Assert.Contains(result.Errors!, e => e.Code == "CategoryCapacityMismatch");
        _inventorySeats.Verify(i => i.EnableSeatsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<SeatPosition>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Enable_WhenInventoryReportsTheLayoutLocked_IsConflictAndNothingIsCommitted()
    {
        var layout = Layout();
        Returns(layout);
        _inventorySeats.Setup(i => i.EnableSeatsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<SeatPosition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EnableSeatsResult.Locked);

        var result = await _service.EnableAsync(layout.Id, "admin");

        Assert.Equal(SeatMapStatus.Conflict, result.Status);
        Assert.Equal("LayoutLocked", result.Code);
        _seatMaps.Verify(r => r.CompleteEnableAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    [Fact]
    public async Task Enable_WhenInventorySeatCallFails_IsUnavailableAndLayoutStaysDraft()
    {
        var layout = Layout();
        Returns(layout);
        _inventorySeats.Setup(i => i.EnableSeatsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<SeatPosition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EnableSeatsResult.Unavailable);

        var result = await _service.EnableAsync(layout.Id, "admin");

        Assert.Equal(SeatMapStatus.Unavailable, result.Status);
        _seatMaps.Verify(r => r.CompleteEnableAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    [Fact]
    public async Task Enable_WhenStockInitialisationFails_IsUnavailable()
    {
        var layout = Layout();
        Returns(layout);
        _inventory.Setup(i => i.InitializeShowStockAsync(It.IsAny<Guid>(), It.IsAny<InitializeShowStockRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InventoryUnavailableException("down"));

        var result = await _service.EnableAsync(layout.Id, "admin");

        Assert.Equal(SeatMapStatus.Unavailable, result.Status);
        _inventorySeats.Verify(i => i.EnableSeatsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<SeatPosition>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Enable_RetryAfterACommitFailure_IsSafe()
    {
        // First attempt: Inventory succeeded but the commit reports the request closed; the retry
        // sends the same version again, which Inventory treats as already enabled.
        var layout = Layout();
        Returns(layout);
        _seatMaps.SetupSequence(r => r.CompleteEnableAsync(layout.Id, "admin", It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(CompleteEnableOutcome.NotEnableable)
            .ReturnsAsync(CompleteEnableOutcome.Enabled);

        var first = await _service.EnableAsync(layout.Id, "admin");
        var second = await _service.EnableAsync(layout.Id, "admin");

        Assert.Equal(SeatMapStatus.Conflict, first.Status);
        Assert.Equal(SeatMapStatus.Ok, second.Status);
        _inventorySeats.Verify(i => i.EnableSeatsAsync(_show.Id, 1, It.IsAny<IReadOnlyList<SeatPosition>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ---- admin: reject / list ----

    [Fact]
    public async Task Reject_NeedsAReason()
    {
        Assert.Equal(SeatMapStatus.Invalid, (await _service.RejectAsync(Guid.NewGuid(), " ", "admin")).Status);
        Assert.Equal(SeatMapStatus.Invalid, (await _service.RejectAsync(Guid.NewGuid(), null, "admin")).Status);
        _seatMaps.Verify(r => r.RejectAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    [Theory]
    [InlineData(RejectOutcome.NotPending, SeatMapStatus.Conflict)]
    [InlineData(RejectOutcome.NotFound, SeatMapStatus.NotFound)]
    public async Task Reject_MapsRepositoryOutcomes(RejectOutcome outcome, SeatMapStatus expected)
    {
        _seatMaps.Setup(r => r.RejectAsync(It.IsAny<Guid>(), "Venue plan missing", "admin", It.IsAny<DateTimeOffset>())).ReturnsAsync(outcome);

        Assert.Equal(expected, (await _service.RejectAsync(Guid.NewGuid(), " Venue plan missing ", "admin")).Status);
    }

    [Fact]
    public async Task List_RejectsAnUnknownStatusFilter()
    {
        Assert.Equal(SeatMapStatus.Invalid, (await _service.ListAsync("Whatever")).Status);
        _seatMaps.Setup(r => r.ListRequestsAsync("Pending", It.IsAny<int>())).ReturnsAsync(new List<SeatMapRequestSummary>());
        Assert.Equal(SeatMapStatus.Ok, (await _service.ListAsync("Pending")).Status);
    }
}
