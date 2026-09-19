using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WaitingRoom.Service.Services;

namespace WaitingRoom.Service.Controllers;

[ApiController]
[Route("api/waiting-room/queues")]
public class QueueController : ControllerBase
{
    private readonly IQueueService _queueService;

    public QueueController(IQueueService queueService)
    {
        _queueService = queueService;
    }

    [HttpPost("{showId}/entries")]
    [Authorize]
    [ProducesResponseType(typeof(QueueEntryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Join(Guid showId)
    {
        var customerSub = GetCustomerSub();
        var entry = await _queueService.JoinAsync(showId, customerSub);

        if (entry is null)
        {
            return Problem(
                detail: $"Show '{showId}' has no waiting room queue open yet.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Queue not found");
        }

        return Ok(_queueService.ToResponse(entry));
    }

    private string GetCustomerSub() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("sub")?.Value
        ?? string.Empty;
}
