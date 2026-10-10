using System;

namespace Booking.Service.Models;

public class Ticket
{
    public required Guid Id { get; init; }
    public required Guid OrderId { get; init; }
    public required Guid CategoryId { get; init; }
    public required Guid ShowId { get; init; }
    public required string CustomerSub { get; init; }
    public required string UniqueCode { get; init; }
    public required decimal Price { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public DateTimeOffset? UsedAt { get; set; }
    public string? UsedBy { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
}

public record TicketResponse(
    Guid Id,
    Guid OrderId,
    Guid ShowId,
    Guid CategoryId,
    string CustomerSub,
    string UniqueCode,
    decimal Price,
    DateTimeOffset IssuedAt,
    DateTimeOffset? UsedAt,
    string? UsedBy,
    DateTimeOffset? VoidedAt = null
);
