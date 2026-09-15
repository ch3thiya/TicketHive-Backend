using System;

namespace Inventory.Service.Models;

public class StockItem
{
    public Guid ShowId { get; set; }
    public Guid CategoryId { get; set; }
    public int Capacity { get; set; }
    public int Available { get; set; }
    public decimal UnitPrice { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string AllocationMode { get; set; } = "GA";
}
