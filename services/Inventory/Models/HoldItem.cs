using System;

namespace Inventory.Service.Models;

public class HoldItem
{
    public Guid HoldId { get; set; }
    public Guid CategoryId { get; set; }
    public int Quantity { get; set; }

    // Snapshotted from stock at hold time (ADR-004) so a later price change
    // never affects an existing hold.
    public decimal UnitPrice { get; set; }

    // Not persisted on hold_items (currency doesn't change once a category
    // is initialized); joined from stock when a hold is read.
    public string Currency { get; set; } = string.Empty;
}
