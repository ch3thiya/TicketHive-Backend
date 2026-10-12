using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Booking.Service.Clients;
using Booking.Service.Db;
using Booking.Service.Models;
using Booking.Service.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Booking.Service.Controllers;

[ApiController]
[Route("api/booking")]
public class TicketsController : ControllerBase
{
    private readonly ITicketService _ticketService;
    private readonly ITicketRepository _ticketRepository;
    private readonly IEntryAccessClient _entryAccess;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TicketsController> _logger;

    public TicketsController(
        ITicketService ticketService,
        ITicketRepository ticketRepository,
        IEntryAccessClient entryAccess,
        TimeProvider timeProvider,
        ILogger<TicketsController> logger)
    {
        _ticketService = ticketService;
        _ticketRepository = ticketRepository;
        _entryAccess = entryAccess;
        _timeProvider = timeProvider;
        _logger = logger;
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

    /// <summary>
    /// Marks a ticket as used at the door. Only an admin, or the organizer that owns the show,
    /// may do this; ownership is checked with Catalog on every request. A suspended organizer
    /// keeps the right because suspension never voids tickets. If ownership cannot be verified
    /// the request is refused (503), never allowed.
    /// </summary>
    [HttpPost("tickets/{code}/validate")]
    [Authorize(Roles = "Organizer,organizer,Organizers,organizers,Admin,admin,Admins,admins")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ValidateTicket(string code)
    {
        var validatorSub = GetCustomerSub();
        if (string.IsNullOrWhiteSpace(validatorSub))
        {
            return Unauthorized();
        }

        var ticket = await _ticketRepository.GetByCodeAsync(code);
        if (ticket is null)
        {
            return Problem(detail: "Ticket code was not recognised.", statusCode: StatusCodes.Status404NotFound, title: "Ticket not recognised");
        }

        // Ownership comes before any ticket status, so a caller who may not scan this show learns
        // nothing about whether the ticket is used or voided.
        if (!IsAdmin())
        {
            var access = await _entryAccess.CheckAsync(ticket.ShowId, validatorSub, HttpContext.RequestAborted);
            if (access == EntryAccessDecision.Unavailable)
            {
                Response.Headers.RetryAfter = "5";
                return Problem(
                    detail: "Ticket validation could not be authorised right now. Please try again shortly.",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Authorisation unavailable");
            }

            if (access == EntryAccessDecision.Denied)
            {
                _logger.LogWarning("Ticket validation refused for {ValidatorSub}: not the organizer of show {ShowId}", validatorSub, ticket.ShowId);
                return Problem(
                    detail: "You are not allowed to validate tickets for this show.",
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Not allowed");
            }
        }

        if (ticket.VoidedAt is not null)
            return Problem(statusCode: 409, title: "Ticket cancelled", detail: "This ticket has been voided and cannot be used.");

        if (ticket.UsedAt is not null)
        {
            // Deliberately omits who scanned it: that is another user's identifier.
            return Problem(
                detail: $"Ticket was already used at {ticket.UsedAt:o}.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Ticket already used");
        }

        var now = _timeProvider.GetUtcNow();
        var success = await _ticketRepository.ValidateAndUseTicketAsync(code, validatorSub, now);
        if (!success)
        {
            return Problem(detail: "Ticket is used, cancelled, or its order is not confirmed.", statusCode: StatusCodes.Status409Conflict, title: "Ticket is not valid");
        }

        _logger.LogInformation("Ticket {TicketId} validated for show {ShowId} by {ValidatorSub}", ticket.Id, ticket.ShowId, validatorSub);
        return Ok(new
        {
            message = "Ticket validated and marked as used.",
            uniqueCode = ticket.UniqueCode,
            showId = ticket.ShowId,
            usedAt = now
        });
    }

    private bool IsAdmin() =>
        User.IsInRole("Admin") || User.IsInRole("admin") || User.IsInRole("Admins") || User.IsInRole("admins");
    private string GetCustomerSub() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("sub")?.Value
        ?? string.Empty;
}
