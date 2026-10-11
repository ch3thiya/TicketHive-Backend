using System.Security.Claims;
using Booking.Service.Clients;
using Booking.Service.Models;
using Booking.Service.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Booking.Service.Controllers;

[ApiController]
public class SalesController(ISalesService sales, IEntryAccessClient entryAccess, ILogger<SalesController> logger) : ControllerBase
{
    public record SalesCategoryResponse(Guid CategoryId, int Sold, decimal Revenue, int Used);

    public record ShowSalesResponse(
        Guid ShowId,
        string Currency,
        int TicketsSold,
        decimal Revenue,
        int TicketsUsed,
        IReadOnlyList<SalesCategoryResponse> Categories,
        DateTimeOffset AsOf);

    /// <summary>
    /// Read-only sales for one show: confirmed tickets sold, revenue and entry progress. Only the
    /// organizer that owns the show may read it (admins and other organizers are refused). Ownership
    /// is checked with Catalog on every request and, if it cannot be verified, the request is
    /// refused (503), never allowed. A suspended organizer keeps read access to their own figures.
    /// </summary>
    [HttpGet("api/booking/shows/{showId:guid}/sales")]
    [Authorize(Roles = "Organizer,organizer,Organizers,organizers")]
    [ProducesResponseType(typeof(ShowSalesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetShowSales(Guid showId)
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(sub))
        {
            return Unauthorized();
        }

        // Ownership first, so a caller who does not own the show learns nothing about its sales.
        var access = await entryAccess.CheckAsync(showId, sub, HttpContext.RequestAborted);
        if (access == EntryAccessDecision.Unavailable)
        {
            Response.Headers.RetryAfter = "5";
            return Problem(
                detail: "Sales could not be authorised right now. Please try again shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Authorisation unavailable");
        }

        if (access == EntryAccessDecision.Denied)
        {
            logger.LogWarning("Sales refused for {Sub}: not the organizer of show {ShowId}", sub, showId);
            return Problem(
                detail: "You can only view sales for your own shows.",
                statusCode: StatusCodes.Status403Forbidden,
                title: "Not allowed");
        }

        var result = await sales.GetShowSalesAsync(showId);
        if (result.Outcome == SalesOutcome.MixedCurrencies)
        {
            logger.LogError("Show {ShowId} has confirmed orders in more than one currency; sales were not totalled", showId);
            return Problem(
                detail: "This show has orders in more than one currency, so its sales cannot be totalled.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Mixed currencies",
                extensions: new Dictionary<string, object?> { ["code"] = "MixedCurrencies" });
        }

        var s = result.Sales!;
        logger.LogInformation("Sales read for show {ShowId}: {TicketsSold} sold, {TicketsUsed} used", showId, s.TicketsSold, s.TicketsUsed);
        return Ok(new ShowSalesResponse(
            s.ShowId, s.Currency, s.TicketsSold, s.Revenue, s.TicketsUsed,
            s.Categories.Select(c => new SalesCategoryResponse(c.CategoryId, c.Sold, c.Revenue, c.Used)).ToList(),
            s.AsOf));
    }
}
