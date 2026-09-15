using System;

namespace Inventory.Service.Models;

public class ShowRules
{
    public Guid ShowId { get; set; }
    public Guid OrganizerId { get; set; }
    public DateTimeOffset? OnSaleAt { get; set; }
    public int MaxPerCustomer { get; set; }
    public int HoldMinutes { get; set; }
    public bool HighDemand { get; set; }
}
