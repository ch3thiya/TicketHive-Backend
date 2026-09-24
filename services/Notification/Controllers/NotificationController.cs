using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Notification.Service.Db;
using Notification.Service.Models;
using Notification.Service.Services;

namespace Notification.Service.Controllers;

public record SendTestEmailRequest(string Email, string CustomerName, decimal Amount, List<string>? TicketCodes);

[ApiController]
[Route("api/notification")]
public class NotificationController : ControllerBase
{
    private readonly INotificationRepository _repository;
    private readonly IEmailService _emailService;
    private readonly TimeProvider _timeProvider;

    public NotificationController(
        INotificationRepository repository,
        IEmailService emailService,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _emailService = emailService;
        _timeProvider = timeProvider;
    }

    [HttpGet("orders/{orderId:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<NotificationRecord>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOrderNotifications(Guid orderId)
    {
        var records = await _repository.GetByOrderIdAsync(orderId);
        return Ok(records);
    }

    [HttpPost("send-test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SendTestNotification([FromBody] SendTestEmailRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest("Email address is required.");
        }

        var testOrderId = Guid.CreateVersion7();
        var ticketCodes = request.TicketCodes ?? new List<string> { "TKT-TEST-1234-5678" };

        var result = await _emailService.SendTicketConfirmationAsync(
            request.Email,
            request.CustomerName ?? "Test Customer",
            testOrderId,
            request.Amount > 0 ? request.Amount : 50.00m,
            "LKR",
            ticketCodes
        );

        var now = _timeProvider.GetUtcNow();
        var record = new NotificationRecord
        {
            Id = Guid.CreateVersion7(),
            OrderId = testOrderId,
            CustomerEmail = request.Email,
            Subject = "Test Ticket Confirmation",
            Status = result.Success ? "Sent" : "Failed",
            ErrorMessage = result.ErrorMessage,
            SentAt = now
        };

        await _repository.CreateAsync(record);

        return Ok(new
        {
            success = result.Success,
            errorMessage = result.ErrorMessage,
            notificationId = record.Id,
            orderId = testOrderId,
            sentAt = now
        });
    }
}
