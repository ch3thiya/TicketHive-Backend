using System;

namespace Identity.Service.Models;

public class UserAccount
{
    public Guid Id { get; set; }
    public string Wso2Sub { get; set; } = string.Empty; // Subject identifier from WSO2
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty; // 'Customer', 'Organizer', 'Admin'
    public string ApprovalStatus { get; set; } = "pending"; // 'pending', 'approved', 'rejected'
    public DateTime CreatedAt { get; set; }
}
