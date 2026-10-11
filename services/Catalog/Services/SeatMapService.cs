using Catalog.Service.Clients;
using Catalog.Service.Db;
using Catalog.Service.Models;
using Microsoft.Extensions.Options;

namespace Catalog.Service.Services;

public class SeatMapService(
    ISeatMapRepository seatMaps,
    IEventRepository events,
    IInventoryClient inventory,
    IInventorySeatClient inventorySeats,
    IOptions<PublishDefaultsOptions> publishDefaults,
    TimeProvider clock,
    ILogger<SeatMapService> logger) : ISeatMapService
{
    private const string Currency = "LKR";
    private const int MaxDescriptionLength = 1000;
    private const int MaxReasonLength = 500;
    private const int ListLimit = 200;

    private static readonly string[] Statuses = { SeatMapStatuses.Pending, SeatMapStatuses.Completed, SeatMapStatuses.Rejected };

    public async Task<SeatMapResult<SeatMapRequest>> RequestAsync(Guid organizerId, Guid showId, string? description)
    {
        var text = description?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > MaxDescriptionLength)
        {
            return SeatMapResult<SeatMapRequest>.Fail(SeatMapStatus.Invalid, $"Describe the seating for your venue in 1 to {MaxDescriptionLength} characters.");
        }

        var show = await events.GetShowByIdAsync(showId);
        if (show is null)
        {
            return SeatMapResult<SeatMapRequest>.Fail(SeatMapStatus.NotFound, "Show not found.");
        }

        var evt = await events.GetEventByIdAsync(show.EventId);
        if (evt is null || evt.OrganizerId != organizerId)
        {
            return SeatMapResult<SeatMapRequest>.Fail(SeatMapStatus.Forbidden, "You do not own this show.");
        }

        if (show.Status == "Cancelled")
        {
            return SeatMapResult<SeatMapRequest>.Fail(SeatMapStatus.Conflict, "A cancelled show cannot have a seat map.");
        }

        var request = new SeatMapRequest(Guid.CreateVersion7(), showId, organizerId, text, SeatMapStatuses.Pending, null, clock.GetUtcNow(), null, null);
        switch (await seatMaps.CreateRequestAsync(request))
        {
            case CreateSeatMapRequestOutcome.Created:
                logger.LogInformation("Seat map {RequestId} requested for show {ShowId}", request.Id, showId);
                return SeatMapResult<SeatMapRequest>.Ok(request);
            case CreateSeatMapRequestOutcome.AlreadySeated:
                return SeatMapResult<SeatMapRequest>.Fail(SeatMapStatus.Conflict, "This show already has a seat map.", "AlreadySeated");
            case CreateSeatMapRequestOutcome.PendingExists:
                return SeatMapResult<SeatMapRequest>.Fail(SeatMapStatus.Conflict, "A seat map request for this show is already pending.", "RequestPending");
            default:
                return SeatMapResult<SeatMapRequest>.Fail(SeatMapStatus.NotFound, "Show not found.");
        }
    }

    public async Task<SeatMapResult<IReadOnlyList<SeatMapRequest>>> GetForOrganizerAsync(Guid organizerId, Guid showId)
    {
        var show = await events.GetShowByIdAsync(showId);
        if (show is null)
        {
            return SeatMapResult<IReadOnlyList<SeatMapRequest>>.Fail(SeatMapStatus.NotFound, "Show not found.");
        }

        var evt = await events.GetEventByIdAsync(show.EventId);
        if (evt is null || evt.OrganizerId != organizerId)
        {
            return SeatMapResult<IReadOnlyList<SeatMapRequest>>.Fail(SeatMapStatus.Forbidden, "You do not own this show.");
        }

        return SeatMapResult<IReadOnlyList<SeatMapRequest>>.Ok(await seatMaps.GetRequestsForShowAsync(showId));
    }

    public async Task<SeatMapResult<IReadOnlyList<SeatMapRequestSummary>>> ListAsync(string? status)
    {
        if (status is not null && !Statuses.Contains(status))
        {
            return SeatMapResult<IReadOnlyList<SeatMapRequestSummary>>.Fail(SeatMapStatus.Invalid, "Status must be Pending, Completed or Rejected.");
        }

        return SeatMapResult<IReadOnlyList<SeatMapRequestSummary>>.Ok(await seatMaps.ListRequestsAsync(status, ListLimit));
    }

    public async Task<SeatMapResult<SeatMapDetail>> GetDetailAsync(Guid requestId)
    {
        var request = await seatMaps.GetRequestAsync(requestId);
        var show = request is null ? null : await events.GetShowByIdAsync(request.ShowId);
        var evt = show is null ? null : await events.GetEventByIdAsync(show.EventId);
        if (request is null || show is null || evt is null)
        {
            return SeatMapResult<SeatMapDetail>.Fail(SeatMapStatus.NotFound, "Seat map request not found.");
        }

        var categories = (await events.GetTicketCategoriesByShowIdAsync(show.Id)).Where(c => c.IsActive).ToList();
        var layouts = await seatMaps.GetLayoutsForShowAsync(show.Id);
        return SeatMapResult<SeatMapDetail>.Ok(new SeatMapDetail(request, evt.Name, show.ShowDate, show.ShowTime, show.SeatingMode, categories, layouts));
    }

    public async Task<SeatMapResult<SeatLayout>> CreateDraftAsync(Guid requestId, SeatLayoutSpec spec, string adminSub)
    {
        var request = await seatMaps.GetRequestAsync(requestId);
        if (request is null)
        {
            return SeatMapResult<SeatLayout>.Fail(SeatMapStatus.NotFound, "Seat map request not found.");
        }

        if (request.Status == SeatMapStatuses.Rejected)
        {
            return SeatMapResult<SeatLayout>.Fail(SeatMapStatus.Conflict, "A rejected request cannot take a layout.", "RequestNotOpen");
        }

        var show = await events.GetShowByIdAsync(request.ShowId);
        if (show is null)
        {
            return SeatMapResult<SeatLayout>.Fail(SeatMapStatus.NotFound, "Show not found.");
        }

        var errors = SeatLayoutRules.Validate(spec, await events.GetTicketCategoriesByShowIdAsync(show.Id));
        if (errors.Count > 0)
        {
            return new SeatMapResult<SeatLayout>(SeatMapStatus.Invalid, null, "The layout is not valid.", errors);
        }

        if (show.SeatingMode == SeatingModes.Seated)
        {
            var locked = await CheckLockAsync(show.Id);
            if (locked is not null)
            {
                return locked;
            }
        }

        var (outcome, layout) = await seatMaps.CreateDraftAsync(requestId, spec, adminSub, clock.GetUtcNow());
        return outcome switch
        {
            CreateDraftOutcome.Created => SeatMapResult<SeatLayout>.Ok(layout!),
            CreateDraftOutcome.RequestNotPending => SeatMapResult<SeatLayout>.Fail(SeatMapStatus.Conflict, "The request is no longer open.", "RequestNotOpen"),
            _ => SeatMapResult<SeatLayout>.Fail(SeatMapStatus.NotFound, "Seat map request not found.")
        };
    }

    public async Task<SeatMapResult<SeatLayout>> EnableAsync(Guid layoutId, string adminSub)
    {
        var layout = await seatMaps.GetLayoutAsync(layoutId);
        if (layout is null)
        {
            return SeatMapResult<SeatLayout>.Fail(SeatMapStatus.NotFound, "Layout not found.");
        }

        if (layout.Status == SeatLayoutStatuses.Enabled)
        {
            return SeatMapResult<SeatLayout>.Ok(layout);
        }

        if (layout.Status != SeatLayoutStatuses.Draft)
        {
            return SeatMapResult<SeatLayout>.Fail(SeatMapStatus.Conflict, "Only a draft layout can be enabled.", "LayoutNotDraft");
        }

        var show = await events.GetShowByIdAsync(layout.ShowId);
        var evt = show is null ? null : await events.GetEventByIdAsync(show.EventId);
        if (show is null || evt is null)
        {
            return SeatMapResult<SeatLayout>.Fail(SeatMapStatus.NotFound, "Show not found.");
        }

        if (show.Status == "Cancelled")
        {
            return SeatMapResult<SeatLayout>.Fail(SeatMapStatus.Conflict, "A cancelled show cannot have a seat map.");
        }

        // Categories may have changed since the draft was saved; the layout must match them now.
        var categories = await events.GetTicketCategoriesByShowIdAsync(show.Id);
        var errors = SeatLayoutRules.Validate(layout.Spec, categories);
        if (errors.Count > 0)
        {
            return new SeatMapResult<SeatLayout>(SeatMapStatus.Invalid, null, "The layout is not valid.", errors);
        }

        try
        {
            // Inventory needs the show's stock before it can hold seats; this is the same idempotent
            // call publishing makes, and it never resets existing stock.
            var defaults = publishDefaults.Value;
            await inventory.InitializeShowStockAsync(show.Id, new InitializeShowStockRequest(
                OrganizerId: evt.OrganizerId,
                OnSaleAt: show.OnSaleAt.HasValue ? new DateTimeOffset(show.OnSaleAt.Value, TimeSpan.Zero) : null,
                MaxPerCustomer: defaults.MaxPerCustomer,
                HoldMinutes: defaults.HoldMinutes,
                HighDemand: show.HighDemandThreshold.HasValue && show.HighDemandThreshold.Value > 0,
                Categories: categories.Where(c => c.IsActive).Select(c => new InitializeShowStockCategory(c.Id, c.Capacity, c.Price, Currency, SeatingModes.Seated)).ToList(),
                HighDemandThreshold: show.HighDemandThreshold));
        }
        catch (InventoryUnavailableException)
        {
            return SeatMapResult<SeatLayout>.Fail(SeatMapStatus.Unavailable, "Inventory is unavailable. Try again shortly.");
        }

        var seats = SeatLayoutRules.Expand(layout.Spec).ToList();
        var enabled = await inventorySeats.EnableSeatsAsync(show.Id, layout.Version, seats);
        switch (enabled)
        {
            case EnableSeatsResult.Locked:
                return SeatMapResult<SeatLayout>.Fail(SeatMapStatus.Conflict, "Seats have been held or sold for this show, so its layout can no longer change.", "LayoutLocked");
            case EnableSeatsResult.Invalid:
                return SeatMapResult<SeatLayout>.Fail(SeatMapStatus.Invalid, "Inventory rejected the layout; its seat counts do not match stock.");
            case EnableSeatsResult.Conflict:
                return SeatMapResult<SeatLayout>.Fail(SeatMapStatus.Conflict, "A different layout version is already active for this show.", "LayoutVersionConflict");
            case EnableSeatsResult.Unavailable:
                return SeatMapResult<SeatLayout>.Fail(SeatMapStatus.Unavailable, "Inventory is unavailable. Try again shortly.");
        }

        var outcome = await seatMaps.CompleteEnableAsync(layoutId, adminSub, clock.GetUtcNow());
        if (outcome is CompleteEnableOutcome.NotEnableable or CompleteEnableOutcome.LayoutNotFound)
        {
            return SeatMapResult<SeatLayout>.Fail(SeatMapStatus.Conflict, "The request is no longer open.", "RequestNotOpen");
        }

        logger.LogInformation("Seat layout {LayoutId} version {Version} enabled for show {ShowId} by {Admin}", layoutId, layout.Version, show.Id, adminSub);
        return SeatMapResult<SeatLayout>.Ok((await seatMaps.GetLayoutAsync(layoutId))!);
    }

    public async Task<SeatMapResult<SeatMapRequest>> RejectAsync(Guid requestId, string? reason, string adminSub)
    {
        var text = reason?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > MaxReasonLength)
        {
            return SeatMapResult<SeatMapRequest>.Fail(SeatMapStatus.Invalid, $"Give a reason of 1 to {MaxReasonLength} characters.");
        }

        switch (await seatMaps.RejectAsync(requestId, text, adminSub, clock.GetUtcNow()))
        {
            case RejectOutcome.Rejected:
                logger.LogInformation("Seat map request {RequestId} rejected by {Admin}", requestId, adminSub);
                return SeatMapResult<SeatMapRequest>.Ok((await seatMaps.GetRequestAsync(requestId))!);
            case RejectOutcome.NotPending:
                return SeatMapResult<SeatMapRequest>.Fail(SeatMapStatus.Conflict, "Only a pending request can be rejected.", "RequestNotPending");
            default:
                return SeatMapResult<SeatMapRequest>.Fail(SeatMapStatus.NotFound, "Seat map request not found.");
        }
    }

    // Null means the layout may still change; otherwise the result to return (locked, or fail closed).
    private async Task<SeatMapResult<SeatLayout>?> CheckLockAsync(Guid showId)
    {
        var lockInfo = await inventorySeats.GetLockAsync(showId);
        if (lockInfo is null)
        {
            return SeatMapResult<SeatLayout>.Fail(SeatMapStatus.Unavailable, "Inventory is unavailable. Try again shortly.");
        }

        return lockInfo.Locked
            ? SeatMapResult<SeatLayout>.Fail(SeatMapStatus.Conflict, "Seats have been held or sold for this show, so its layout can no longer change.", "LayoutLocked")
            : null;
    }
}
