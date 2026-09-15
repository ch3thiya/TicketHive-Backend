using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Inventory.Service.Models;
using Inventory.Service.Services;

namespace Inventory.Service.Controllers;

[ApiController]
[Route("api/inventory/shows/{showId}/waiting-room")]
public class WaitingRoomController : ControllerBase
{
    private readonly IWaitingRoomService _waitingRoomService;

    public WaitingRoomController(IWaitingRoomService waitingRoomService)
    {
        _waitingRoomService = waitingRoomService;
    }

    [HttpPost("join")]
    [Authorize]
    [ProducesResponseType(typeof(QueueStatusResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> JoinQueue(Guid showId)
    {
        var customerSub = GetCustomerSub();
        var status = await _waitingRoomService.JoinQueueAsync(showId, customerSub);
        return Ok(status);
    }

    [HttpGet("status")]
    [Authorize]
    [ProducesResponseType(typeof(QueueStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetQueueStatus(Guid showId)
    {
        var customerSub = GetCustomerSub();
        var status = await _waitingRoomService.GetQueueStatusAsync(showId, customerSub);

        if (status is null)
        {
            return Problem(
                detail: $"No active waiting room entry found for show '{showId}'.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Waiting room entry not found");
        }

        return Ok(status);
    }

    [HttpPost("admit")]
    [Authorize(Roles = "Admin,Organizer")]
    [ProducesResponseType(typeof(AdmitCustomersResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> AdmitNextCustomers(Guid showId, [FromBody] AdmitCustomersRequest request)
    {
        var result = await _waitingRoomService.AdmitNextCustomersAsync(
            showId,
            request.BatchSize,
            request.TokenDurationMinutes);

        return Ok(result);
    }

    private string GetCustomerSub() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("sub")?.Value
        ?? string.Empty;
}
