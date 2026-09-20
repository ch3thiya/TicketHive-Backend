using System.Threading.Tasks;
using Booking.Service.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Booking.Service.Controllers;

[ApiController]
[Route("api/booking/payment")]
public class PaymentNotificationController : ControllerBase
{
    private readonly IOrderService _orderService;

    public PaymentNotificationController(IOrderService orderService)
    {
        _orderService = orderService;
    }

    [HttpPost("notify")]
    [Consumes("application/x-www-form-urlencoded", "application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PaymentNotification(
        [FromForm] string? merchant_id,
        [FromForm] string? order_id,
        [FromForm] string? payhere_amount,
        [FromForm] string? payhere_currency,
        [FromForm] string? status_code,
        [FromForm] string? md5sig)
    {
        merchant_id ??= Request.Form["merchant_id"].ToString();
        order_id ??= Request.Form["order_id"].ToString();
        payhere_amount ??= Request.Form["payhere_amount"].ToString();
        payhere_currency ??= Request.Form["payhere_currency"].ToString();
        status_code ??= Request.Form["status_code"].ToString();
        md5sig ??= Request.Form["md5sig"].ToString();

        if (string.IsNullOrWhiteSpace(order_id) || string.IsNullOrWhiteSpace(md5sig))
        {
            return BadRequest("Missing required parameters");
        }

        var processed = await _orderService.ProcessPaymentNotificationAsync(
            merchant_id ?? string.Empty,
            order_id,
            payhere_amount ?? string.Empty,
            payhere_currency ?? string.Empty,
            status_code ?? string.Empty,
            md5sig
        );

        if (!processed)
        {
            return BadRequest("Failed to process notification or invalid signature");
        }

        return Ok();
    }
}
