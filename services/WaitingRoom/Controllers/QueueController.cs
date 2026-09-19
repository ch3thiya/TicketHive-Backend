using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WaitingRoom.Service.Clients;
using WaitingRoom.Service.Models;
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
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Join(Guid showId)
    {
        var customerSub = GetCustomerSub();

        QueueEntry? entry;
        try
        {
            entry = await _queueService.JoinAsync(showId, customerSub);
        }
        catch (CatalogUnavailableException)
        {
            return Problem(
                detail: "Could not determine this show's sales rules right now. Please try again.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Catalog unavailable");
        }

        if (entry is null)
        {
            return Problem(
                detail: $"Show '{showId}' has no waiting room queue open yet.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Queue not found");
        }

        return Ok(_queueService.ToResponse(entry));
    }

    [HttpGet("{showId}/entries/me")]
    [Authorize]
    [ProducesResponseType(typeof(QueuePositionResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPosition(Guid showId)
    {
        var customerSub = GetCustomerSub();
        var position = await _queueService.GetPositionAsync(showId, customerSub);
        return Ok(position);
    }

    private string GetCustomerSub() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("sub")?.Value
        ?? string.Empty;
}
