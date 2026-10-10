using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Payment.Service.Db;
using Payment.Service.Models;

namespace Payment.Service.Controllers;

[ApiController, Authorize(Policy = "CancellationInternal")]
[Route("internal/payment/refunds")]
public class RefundsController(RefundRepository repository, ILogger<RefundsController> logger) : ControllerBase
{
    [HttpPut("{orderId:guid}"), ProducesResponseType(typeof(RefundResponse), 200), ProducesResponseType(409)]
    public async Task<IActionResult> Refund(Guid orderId, RefundRequest request)
    {
        try
        {
            var result = await repository.RefundAsync(orderId, request);
            logger.LogInformation("Refund {OrderId} has outcome {RefundStatus}", orderId, result.Status);
            return Ok(result);
        }
        catch (ArgumentException ex) { return Problem(statusCode: 400, detail: ex.Message); }
        catch (InvalidOperationException ex) { return Problem(statusCode: 409, detail: ex.Message); }
    }
}
