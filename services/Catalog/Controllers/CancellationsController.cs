using Catalog.Service.Authorization;
using Catalog.Service.Clients;
using Catalog.Service.Db;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Catalog.Service.Controllers;

[ApiController]
public class CancellationsController(IEventRepository events, CancellationRepository cancellations, CancellationClient client, Catalog.Service.Services.IEventService service) : ControllerBase
{
    [Authorize(Roles = "admin,admins,Admin,Admins"), HttpPost("api/catalog/admin/shows/{id:guid}/cancel")]
    public async Task<IActionResult> AdminCancelShow(Guid id)
    {
        var show = await events.GetShowByIdAsync(id);
        if (show is null) return NotFound();
        var evt = await events.GetEventByIdAsync(show.EventId);
        if (evt is null) return NotFound();
        await service.CancelShowAsync(evt.OrganizerId, id);
        return Accepted(new { Status = "Processing", ShowId = id });
    }

    [Authorize(Roles = "admin,admins,Admin,Admins"), HttpPost("api/catalog/admin/events/{id:guid}/cancel")]
    public async Task<IActionResult> AdminCancelEvent(Guid id)
    {
        var evt = await events.GetEventByIdAsync(id);
        if (evt is null) return NotFound();
        await service.CancelEventAsync(evt.OrganizerId, id);
        return Accepted(new { Status = "Processing", EventId = id });
    }

    [Authorize(Policy = "CancellationInternal"), HttpGet("internal/catalog/cancellations/shows/{id:guid}/rules")]
    public async Task<IActionResult> Rules(Guid id)
    {
        var show = await events.GetShowByIdAsync(id);
        if (show is null) return NotFound();
        // Catalog stores local show time; all current venues are in Sri Lanka.
        var local = show.ShowDate.ToDateTime(show.ShowTime, DateTimeKind.Unspecified);
        var start = TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.FindSystemTimeZoneById("Asia/Colombo"));
        return Ok(new { StartsAt = new DateTimeOffset(start), show.Status });
    }

    [Authorize(Policy = "ActiveOrganizer"), HttpGet("api/catalog/shows/{id:guid}/cancellation")]
    public async Task<IActionResult> Progress(Guid id)
    {
        var show = await events.GetShowByIdAsync(id);
        if (show is null) return NotFound();
        var evt = await events.GetEventByIdAsync(show.EventId);
        if (evt is null || !HttpContext.Items.TryGetValue(ActiveOrganizerAuthorizationHandler.OrganizerIdItemKey, out var owner) || !Equals(owner, evt.OrganizerId))
            return Forbid();
        try { return Ok(new { SalesStopped = await cancellations.SalesStoppedAsync(id), Orders = await client.ProgressAsync(id) }); }
        catch (HttpRequestException) { return Problem(statusCode: 503, detail: "Cancellation progress is temporarily unavailable."); }
    }
}
