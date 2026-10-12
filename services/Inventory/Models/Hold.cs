using System;
using System.Collections.Generic;

namespace Inventory.Service.Models;

public class Hold
{
    public Guid Id { get; set; }
    public Guid ShowId { get; set; }
    public string CustomerSub { get; set; } = string.Empty;
    public HoldStatus Status { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public List<HoldItem> Items { get; set; } = new();

    // Empty for general-admission holds. Seated holds list the exact seats (ADR-027).
    public List<HoldSeat> Seats { get; set; } = new();

    // The expiry timestamp is the truth; the sweeper is cleanup. A hold
    // reads as Expired once its time has passed even if the sweeper has
    // not reached it yet, without mutating the stored row.
    public HoldStatus EffectiveStatus(DateTimeOffset now) =>
        (Status == HoldStatus.Active || Status == HoldStatus.PaymentPending) && ExpiresAt <= now ? HoldStatus.Expired : Status;
}
