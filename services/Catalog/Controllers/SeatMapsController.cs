using System.Security.Claims;
using Catalog.Service.Authorization;
using Catalog.Service.Models;
using Catalog.Service.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Catalog.Service.Controllers;

[ApiController]
public class SeatMapsController(ISeatMapService seatMaps) : ControllerBase
{
    private const string AdminRoles = "admin,admins,Admin,Admins";

    [HttpPost("api/catalog/shows/{showId:guid}/seat-map-requests")]
    [Authorize(Policy = "ActiveOrganizer")]
    [ProducesResponseType(typeof(SeatMapRequest), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Request(Guid showId, [FromBody] SeatMapRequestBody body)
    {
        var result = await seatMaps.RequestAsync(CurrentOrganizerId(), showId, body.Description);
        return result.Status == SeatMapStatus.Ok
            ? Created($"api/catalog/shows/{showId}/seat-map-requests", result.Value)
            : ToProblem(result);
    }

    [HttpGet("api/catalog/shows/{showId:guid}/seat-map-requests")]
    [Authorize(Policy = "OrganizerRead")]
    [ProducesResponseType(typeof(IReadOnlyList<SeatMapRequest>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ForShow(Guid showId)
    {
        var result = await seatMaps.GetForOrganizerAsync(CurrentOrganizerId(), showId);
        return result.Status == SeatMapStatus.Ok ? Ok(result.Value) : ToProblem(result);
    }

    [HttpGet("api/catalog/admin/seat-map-requests")]
    [Authorize(Roles = AdminRoles)]
    [ProducesResponseType(typeof(IReadOnlyList<SeatMapRequestSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] string? status)
    {
        var result = await seatMaps.ListAsync(status);
        return result.Status == SeatMapStatus.Ok ? Ok(result.Value) : ToProblem(result);
    }

    [HttpGet("api/catalog/admin/seat-map-requests/{requestId:guid}")]
    [Authorize(Roles = AdminRoles)]
    [ProducesResponseType(typeof(SeatMapDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Detail(Guid requestId)
    {
        var result = await seatMaps.GetDetailAsync(requestId);
        return result.Status == SeatMapStatus.Ok ? Ok(result.Value) : ToProblem(result);
    }

    [HttpPost("api/catalog/admin/seat-map-requests/{requestId:guid}/layouts")]
    [Authorize(Roles = AdminRoles)]
    [ProducesResponseType(typeof(SeatLayout), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateLayout(Guid requestId, [FromBody] SeatLayoutBody body)
    {
        if (body.Sections is null)
        {
            return Problem(detail: "The layout needs a list of sections.", statusCode: StatusCodes.Status400BadRequest, title: "Invalid layout");
        }

        var spec = new SeatLayoutSpec(body.Sections.Select(s => new SeatLayoutSection(s.Name ?? string.Empty, s.CategoryId, s.Rows, s.SeatsPerRow)).ToList());
        var result = await seatMaps.CreateDraftAsync(requestId, spec, CurrentSub());
        return result.Status == SeatMapStatus.Ok
            ? Created($"api/catalog/admin/seat-map-requests/{requestId}", result.Value)
            : ToProblem(result);
    }

    [HttpPost("api/catalog/admin/seat-layouts/{layoutId:guid}/enable")]
    [Authorize(Roles = AdminRoles)]
    [ProducesResponseType(typeof(SeatLayout), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Enable(Guid layoutId)
    {
        var result = await seatMaps.EnableAsync(layoutId, CurrentSub());
        return result.Status == SeatMapStatus.Ok ? Ok(result.Value) : ToProblem(result);
    }

    [HttpPost("api/catalog/admin/seat-map-requests/{requestId:guid}/reject")]
    [Authorize(Roles = AdminRoles)]
    [ProducesResponseType(typeof(SeatMapRequest), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reject(Guid requestId, [FromBody] RejectBody body)
    {
        var result = await seatMaps.RejectAsync(requestId, body.Reason, CurrentSub());
        return result.Status == SeatMapStatus.Ok ? Ok(result.Value) : ToProblem(result);
    }

    private Guid CurrentOrganizerId() =>
        HttpContext.Items.TryGetValue(ActiveOrganizerAuthorizationHandler.OrganizerIdItemKey, out var value) && value is Guid id
            ? id
            : throw new UnauthorizedAccessException("Organizer identity could not be determined from access token.");

    private string CurrentSub() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "unknown";

    private IActionResult ToProblem<T>(SeatMapResult<T> result)
    {
        var extensions = new Dictionary<string, object?>();
        if (result.Code is not null) extensions["code"] = result.Code;
        if (result.Errors is not null) extensions["errors"] = result.Errors;

        switch (result.Status)
        {
            case SeatMapStatus.NotFound:
                return Problem(detail: result.Detail, statusCode: StatusCodes.Status404NotFound, title: "Not found", extensions: extensions);
            case SeatMapStatus.Forbidden:
                return Problem(detail: result.Detail, statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", extensions: extensions);
            case SeatMapStatus.Conflict:
                return Problem(detail: result.Detail, statusCode: StatusCodes.Status409Conflict, title: "Conflict", extensions: extensions);
            case SeatMapStatus.Unavailable:
                Response.Headers.RetryAfter = "5";
                return Problem(detail: result.Detail, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable", extensions: extensions);
            default:
                return Problem(
                    detail: result.Detail,
                    statusCode: result.Errors is { Count: > 0 } ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status400BadRequest,
                    title: "Invalid request",
                    extensions: extensions);
        }
    }
}

public record SeatMapRequestBody(string? Description);

public record SeatLayoutSectionBody(string? Name, Guid CategoryId, int Rows, int SeatsPerRow);

public record SeatLayoutBody(List<SeatLayoutSectionBody>? Sections);

public record RejectBody(string? Reason);
