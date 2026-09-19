using System;
using System.Collections.Generic;

namespace Inventory.Service.Models;

public enum WaitingRoomStatus
{
    Waiting,
    Admitted,
    Expired,
    Used,
    Left
}

public class WaitingRoomEntry
{
    public Guid Id { get; set; }
    public Guid ShowId { get; set; }
    public required string CustomerSub { get; set; }
    public WaitingRoomStatus Status { get; set; }
    public int Position { get; set; }
    public string? AdmissionToken { get; set; }
    public DateTimeOffset? AdmittedAt { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public record QueueStatusResponse(
    Guid ShowId,
    string CustomerSub,
    string Status,
    int Position,
    int TotalWaiting,
    string? AdmissionToken,
    DateTimeOffset? TokenExpiresAt
);

public record AdmitCustomersRequest(int BatchSize = 10, int TokenDurationMinutes = 10);
public record AdmitCustomersResponse(int AdmittedCount, List<string> AdmittedCustomerSubs);
