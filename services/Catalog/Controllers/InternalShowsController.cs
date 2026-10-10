using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Catalog.Service.Models;
using Catalog.Service.Services;

namespace Catalog.Service.Controllers;

public record SalesEligibilityResponse(Guid ShowId, bool Eligible, string Reason);

public record EntryAccessResponse(Guid ShowId, bool Allowed, string Reason);

[ApiController]
[Route("internal/catalog/shows")]
public class InternalShowsController : ControllerBase
{
    private readonly IEventService _eventService;

    public InternalShowsController(IEventService eventService)
    {
        _eventService = eventService;
    }

    [HttpGet("{showId}/sales-rules")]
    [Authorize(Policy = "InternalService")]
    [ProducesResponseType(typeof(ShowSalesRulesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSalesRules(Guid showId)
    {
        var salesRules = await _eventService.GetSalesRulesAsync(showId);
        if (salesRules is null)
        {
            return Problem(
                detail: $"Show '{showId}' was not found.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Show not found");
        }

        return Ok(salesRules);
    }

    /// <summary>
    /// Tells Inventory whether the show can take new sales. Always checks the organizer's live
    /// status with Identity; answers 503 (never "eligible") when that status cannot be verified.
    /// </summary>
    [HttpGet("{showId:guid}/sales-eligibility")]
    [Authorize(Policy = "InternalService")]
    [ProducesResponseType(typeof(SalesEligibilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetSalesEligibility(Guid showId, [FromServices] ISalesEligibilityService eligibility)
    {
        var result = await eligibility.CheckAsync(showId, HttpContext.RequestAborted);
        return result.Outcome switch
        {
            SalesEligibilityOutcome.ShowNotFound => Problem(
                detail: $"Show '{showId}' was not found.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Show not found"),
            SalesEligibilityOutcome.OrganizerStatusUnavailable => Problem(
                detail: "The organizer's status could not be verified right now.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Organizer status unavailable"),
            SalesEligibilityOutcome.Eligible => Ok(new SalesEligibilityResponse(showId, true, "None")),
            _ => Ok(new SalesEligibilityResponse(showId, false, result.Outcome.ToString()))
        };
    }

    /// <summary>
    /// Tells Booking whether the caller may validate tickets for the show. Only the owning
    /// organizer is allowed (admins are decided by Booking from the token). Checked live on every
    /// call; answers 503, never "allowed", when the organizer cannot be verified.
    /// </summary>
    [HttpGet("{showId:guid}/entry-access")]
    [Authorize(Policy = "InternalService")]
    [ProducesResponseType(typeof(EntryAccessResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetEntryAccess(Guid showId, [FromQuery] string? sub, [FromServices] IEntryAccessService entryAccess)
    {
        if (string.IsNullOrWhiteSpace(sub))
        {
            return Problem(detail: "The 'sub' query parameter is required.", statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var result = await entryAccess.CheckAsync(showId, sub, HttpContext.RequestAborted);
        return result.Outcome switch
        {
            EntryAccessOutcome.ShowNotFound => Problem(
                detail: $"Show '{showId}' was not found.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Show not found"),
            EntryAccessOutcome.OrganizerStatusUnavailable => Problem(
                detail: "The organizer's status could not be verified right now.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Organizer status unavailable"),
            EntryAccessOutcome.Allowed => Ok(new EntryAccessResponse(showId, true, "None")),
            _ => Ok(new EntryAccessResponse(showId, false, result.Outcome.ToString()))
        };
    }
}