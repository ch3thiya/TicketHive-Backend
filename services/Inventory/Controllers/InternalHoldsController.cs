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

    [HttpPatch("{holdId}/freeze")]
    [Authorize(Policy = "InternalService")]
    [ProducesResponseType(typeof(InternalHoldResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> FreezeHold(Guid holdId)
    {
        var hold = await _holdService.GetHoldAsync(holdId);
        if (hold is null)
        {
            return Problem(
                detail: $"Hold '{holdId}' was not found.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Hold not found");
        }

        var success = await _holdService.FreezeHoldAsync(holdId);
        if (!success)
        {
            return Problem(
                detail: $"Hold '{holdId}' cannot be frozen (already expired or not active).",
                statusCode: StatusCodes.Status409Conflict,
                title: "Hold state conflict");
        }

        var updated = await _holdService.GetHoldAsync(holdId);
        return Ok(_holdService.ToInternalResponse(updated!));
    }

    [HttpPatch("{holdId}/convert")]
    [Authorize(Policy = "InternalService")]
    [ProducesResponseType(typeof(InternalHoldResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ConvertHold(Guid holdId)
    {
        var hold = await _holdService.GetHoldAsync(holdId);
        if (hold is null)
        {
            return Problem(
                detail: $"Hold '{holdId}' was not found.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Hold not found");
        }

        var success = await _holdService.ConvertHoldAsync(holdId);
        if (!success)
        {
            return Problem(
                detail: $"Hold '{holdId}' cannot be converted (not in frozen/active state).",
                statusCode: StatusCodes.Status409Conflict,
                title: "Hold state conflict");
        }

        var updated = await _holdService.GetHoldAsync(holdId);
        return Ok(_holdService.ToInternalResponse(updated!));
    }

    [HttpPatch("{holdId}/release")]
    [Authorize(Policy = "InternalService")]
    [ProducesResponseType(typeof(InternalHoldResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReleaseHold(Guid holdId)
    {
        var hold = await _holdService.GetHoldAsync(holdId);
        if (hold is null)
        {
            return Problem(
                detail: $"Hold '{holdId}' was not found.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Hold not found");
        }

        var success = await _holdService.ReleaseHoldAsync(holdId);
        if (!success)
        {
            return Problem(
                detail: $"Hold '{holdId}' cannot be released (already converted or cancelled).",
                statusCode: StatusCodes.Status409Conflict,
                title: "Hold state conflict");
        }

        var updated = await _holdService.GetHoldAsync(holdId);
        return Ok(_holdService.ToInternalResponse(updated!));
    }
}
