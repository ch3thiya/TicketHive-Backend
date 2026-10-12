using Booking.Service.Db;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Booking.Service.Controllers;

[ApiController]
public class ReminderEligibilityController(IReminderEligibilityRepository orders) : ControllerBase
{
    private const int MaxOrders = 200;

    public record ReminderEligibilityRequest(List<Guid>? OrderIds);

    public record ReminderOrderResponse(Guid OrderId, int TicketCount);

    public record ReminderEligibilityResponse(List<ReminderOrderResponse> Orders);

    /// <summary>
    /// Tells Notification which of the given orders are still confirmed and how many live tickets each
    /// has, so a reminder is never sent for a cancelled order. Read-only; only an application token
    /// with booking:read may call it. Orders that are not eligible are simply left out.
    /// </summary>
    [HttpPost("internal/booking/orders/reminder-eligibility")]
    [Authorize(Policy = "BookingInternalRead")]
    [ProducesResponseType(typeof(ReminderEligibilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetEligible([FromBody] ReminderEligibilityRequest request)
    {
        var ids = request.OrderIds?.Where(id => id != Guid.Empty).Distinct().ToList() ?? new List<Guid>();
        if (ids.Count == 0 || ids.Count > MaxOrders)
        {
            return Problem(
                detail: $"Send between 1 and {MaxOrders} order ids.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid request");
        }

        var eligible = await orders.GetEligibleAsync(ids);
        return Ok(new ReminderEligibilityResponse(eligible.Select(o => new ReminderOrderResponse(o.OrderId, o.TicketCount)).ToList()));
    }
}
