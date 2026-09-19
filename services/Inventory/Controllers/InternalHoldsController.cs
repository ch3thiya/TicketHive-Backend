using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Inventory.Service.Services;

namespace Inventory.Service.Controllers;

// For Booking (Sprint 3) to check whether a hold is still valid before
// converting it to an order. Reports EffectiveStatus (S2-03 deferred): a
// hold whose expires_at has passed reads as Expired even if the sweeper has
// not reached it yet, so Booking never has to reason about sweep timing.
[ApiController]
[Route("internal/inventory/holds")]
public class InternalHoldsController : ControllerBase
{
    private readonly IHoldService _holdService;

    public InternalHoldsController(IHoldService holdService)
    {
        _holdService = holdService;
    }

    [HttpGet("{holdId}")]
    [Authorize(Policy = "InternalService")]
    [ProducesResponseType(typeof(InternalHoldResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHold(Guid holdId)
    {
        var hold = await _holdService.GetHoldAsync(holdId);
        if (hold is null)
        {
            return Problem(
                detail: $"Hold '{holdId}' was not found.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Hold not found");
        }

        return Ok(_holdService.ToInternalResponse(hold));
    }
}
