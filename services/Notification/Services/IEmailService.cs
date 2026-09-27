using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Notification.Service.Services;

public record EmailResult(bool Success, string? ErrorMessage);

public interface IEmailService
{
    Task<EmailResult> SendTicketConfirmationAsync(
        string toEmail,
        string customerName,
        Guid orderId,
        decimal totalAmount,
        string currency,
        IReadOnlyList<string> ticketCodes);
}
