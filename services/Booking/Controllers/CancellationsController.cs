using System.Security.Claims;
using Booking.Service.Clients;
using Booking.Service.Db;
using Booking.Service.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Booking.Service.Controllers;

[ApiController]
public class CancellationsController(CancellationRepository cancellations, IOrderRepository orders, CancellationClient client) : ControllerBase
{
    [Authorize, HttpPost("api/booking/orders/{id:guid}/cancel")]
    [ProducesResponseType(typeof(CancellationState), 202), ProducesResponseType(typeof(CancellationState), 200), ProducesResponseType(404), ProducesResponseType(409), ProducesResponseType(503)]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var customer = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        var order = await orders.GetByIdAsync(id);
        if (customer is null || order is null || order.CustomerSub != customer) return NotFound();
        if (order.Status == OrderStatus.Cancelled) return Ok(await cancellations.GetAsync(id));
        try
        {
            var rules = await client.GetRulesAsync(order.ShowId);
            var refusal = await cancellations.CancelAsync(id, customer, rules.StartsAt, false);
            if (refusal is not null) return Problem(statusCode: refusal == "NotFound" ? 404 : 409, detail: refusal);
            return Accepted($"/api/booking/orders/{id}/cancellation", await cancellations.GetAsync(id));
        }
        catch (HttpRequestException) { return Problem(statusCode: 503, detail: "Show details are unavailable. Please retry."); }
    }

    [Authorize, HttpGet("api/booking/orders/{id:guid}/cancellation")]
    [ProducesResponseType(typeof(CancellationState), 200), ProducesResponseType(404)]
    public async Task<IActionResult> Status(Guid id)
    {
        var order = await orders.GetByIdAsync(id);
        var customer = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (order is null || order.CustomerSub != customer) return NotFound();
        var state = await cancellations.GetAsync(id);
        return state is null ? NotFound() : Ok(state);
    }

    [Authorize(Policy = "CancellationInternal"), HttpPut("internal/booking/cancellations/shows/{showId:guid}")]
    public async Task<IActionResult> CancelShow(Guid showId)
    {
        await cancellations.CancelShowAsync(showId);
        return NoContent();
    }

    [Authorize(Policy = "CancellationInternal"), HttpGet("internal/booking/cancellations/shows/{showId:guid}/progress")]
    public async Task<IActionResult> Progress(Guid showId) => Ok(await cancellations.ProgressAsync(showId));
}
