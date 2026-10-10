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
}
