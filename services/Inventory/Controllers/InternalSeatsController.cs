using Inventory.Service.Db;
using Inventory.Service.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Service.Controllers;

[ApiController]
[Authorize(Policy = "InternalService")]
[Route("internal/inventory/shows/{showId:guid}/seats")]
public class InternalSeatsController(ISeatLayoutRepository seats, TimeProvider clock, ILogger<InternalSeatsController> logger) : ControllerBase
{
    private const int MaxSeats = 50_000;

    // Idempotent per show and version: Catalog retries this until it can commit the layout.
    [HttpPut]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Enable(Guid showId, [FromBody] EnableSeatsRequest request)
    {
        if (request.Version < 1 || request.Seats is null || request.Seats.Count == 0 || request.Seats.Count > MaxSeats
            || request.Seats.Any(s => s.CategoryId == Guid.Empty || string.IsNullOrWhiteSpace(s.Section) || string.IsNullOrWhiteSpace(s.Row) || s.Number < 1))
        {
            return Problem(detail: "The seat layout request is invalid.", statusCode: StatusCodes.Status400BadRequest, title: "Invalid seat layout");
        }

        var result = await seats.EnableAsync(showId, request.Version, request.Seats, clock.GetUtcNow());
        logger.LogInformation("Seat layout version {Version} for show {ShowId}: {Outcome}", request.Version, showId, result.Outcome);

        return result.Outcome switch
        {
            EnableSeatsOutcome.Enabled or EnableSeatsOutcome.AlreadyEnabled => NoContent(),
            EnableSeatsOutcome.Mismatch => Problem(detail: result.Detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid seat layout"),
            EnableSeatsOutcome.Locked => Problem(detail: result.Detail, statusCode: StatusCodes.Status409Conflict, title: "Layout locked"),
            EnableSeatsOutcome.StaleVersion => Problem(detail: result.Detail, statusCode: StatusCodes.Status409Conflict, title: "Stale layout version"),
            _ => Problem(detail: result.Detail, statusCode: StatusCodes.Status409Conflict, title: "Show stock is not ready")
        };
    }

    [HttpGet("lock")]
    [ProducesResponseType(typeof(SeatLockState), StatusCodes.Status200OK)]
    public async Task<IActionResult> Lock(Guid showId) => Ok(await seats.GetLockStateAsync(showId));
}

public record EnableSeatsRequest(int Version, List<SeatToInitialize> Seats);
