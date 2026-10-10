using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Booking.Service.Db;
using Booking.Service.Models;
using Booking.Service.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Booking.Service.Controllers;

[ApiController]
[Route("api/booking")]
public class TicketsController : ControllerBase
{
    private readonly ITicketService _ticketService;
    private readonly ITicketRepository _ticketRepository;
    private readonly TimeProvider _timeProvider;

    public TicketsController(
        ITicketService ticketService,
        ITicketRepository ticketRepository,
        TimeProvider timeProvider)
    {
        _ticketService = ticketService;
        _ticketRepository = ticketRepository;
        _timeProvider = timeProvider;
    }

    [HttpGet("tickets")]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyList<TicketResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCustomerTickets()
    {
        var customerSub = GetCustomerSub();
        if (string.IsNullOrWhiteSpace(customerSub))
        {
            return Unauthorized();
        }

        var tickets = await _ticketService.GetCustomerTicketsAsync(customerSub);
        return Ok(tickets);
    }

    [HttpGet("tickets/{id:guid}")]
    [Authorize]
    [ProducesResponseType(typeof(TicketResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTicketById(Guid id)
    {
        var customerSub = GetCustomerSub();
        if (string.IsNullOrWhiteSpace(customerSub))
        {
            return Unauthorized();
        }

        var ticket = await _ticketService.GetTicketByIdAsync(id, customerSub);
        if (ticket is null)
        {
            return Problem(detail: $"Ticket '{id}' was not found.", statusCode: StatusCodes.Status404NotFound, title: "Ticket not found");
        }

        return Ok(ticket);
    }

    [HttpGet("orders/{orderId:guid}/tickets")]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyList<TicketResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetOrderTickets(Guid orderId)
    {
        var customerSub = GetCustomerSub();
        if (string.IsNullOrWhiteSpace(customerSub))
        {
            return Unauthorized();
        }

        var tickets = await _ticketService.GetOrderTicketsAsync(orderId, customerSub);
        return Ok(tickets);
    }

    [HttpPost("tickets/{code}/validate")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ValidateTicket(string code)
    {
        var organizerSub = GetCustomerSub();
        if (string.IsNullOrWhiteSpace(organizerSub))
        {
            return Unauthorized();
        }

        var ticket = await _ticketRepository.GetByCodeAsync(code);
        if (ticket is null)
        {
            return Problem(detail: $"Ticket code '{code}' was not recognised.", statusCode: StatusCodes.Status404NotFound, title: "Ticket not recognised");
        }

        if (ticket.VoidedAt is not null)
            return Problem(statusCode: 409, title: "Ticket cancelled", detail: "This ticket has been voided and cannot be used.");

        if (ticket.UsedAt is not null)
        {
            return Problem(
                detail: $"Ticket was already used at {ticket.UsedAt:o} by {ticket.UsedBy}.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Ticket already used");
        }

        var now = _timeProvider.GetUtcNow();
        var success = await _ticketRepository.ValidateAndUseTicketAsync(code, organizerSub, now);
        if (!success)
        {
            return Problem(detail: "Ticket is used, cancelled, or its order is not confirmed.", statusCode: StatusCodes.Status409Conflict, title: "Ticket is not valid");
        }

        return Ok(new
        {
            message = "Ticket validated and marked as used.",
            uniqueCode = ticket.UniqueCode,
            showId = ticket.ShowId,
            usedAt = now
        });
    }

    private string GetCustomerSub() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("sub")?.Value
        ?? string.Empty;
}
