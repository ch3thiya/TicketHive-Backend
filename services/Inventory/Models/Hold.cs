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
}
