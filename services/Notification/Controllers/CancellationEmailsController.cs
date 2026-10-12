using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Notification.Service.Db;
using Notification.Service.Models;

namespace Notification.Service.Controllers;

[ApiController, Authorize(Policy = "CancellationInternal")]
[Route("internal/notification/cancellations")]
public class CancellationEmailsController(CancellationEmailRepository repository) : ControllerBase
{
    [HttpPut, ProducesResponseType(200)]
    public async Task<IActionResult> Enqueue(CancellationEmail email)
    {
        if (string.IsNullOrWhiteSpace(email.Key) || email.OrderIds.Length == 0) return BadRequest();
        return Ok(new { Status = await repository.EnqueueAsync(email) });
    }

    [HttpGet("status"), ProducesResponseType(200), ProducesResponseType(404)]
    public async Task<IActionResult> Status([FromQuery] string key)
    {
        var status = await repository.GetStatusAsync(key);
        return status is null ? NotFound() : Ok(new
        {
            Status = status,
            ActionRequired = status is "NeedsReconciliation" or "MissingRecipient",
            CanRetryAutomatically = false
        });
    }
}
