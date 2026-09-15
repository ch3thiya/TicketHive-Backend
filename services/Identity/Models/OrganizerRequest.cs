using System;

namespace Identity.Service.Models;

public class OrganizerRequest
{
    public Guid Id { get; set; }
    public Guid UserAccountId { get; set; }

    // Form fields
    public string OrganizationName { get; set; } = string.Empty;
    public string BusinessEmail { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string About { get; set; } = string.Empty;

    // Workflow fields
    public string Status { get; set; } = "pending"; // 'pending', 'approved', 'rejected'
    public DateTime CreatedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
}
