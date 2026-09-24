using System;

namespace Notification.Service.Models;

public class NotificationRecord
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string CustomerEmail { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Status { get; set; } = "Sent"; // 'Sent', 'Failed'
    public string? ErrorMessage { get; set; }
    public DateTimeOffset SentAt { get; set; }
}
