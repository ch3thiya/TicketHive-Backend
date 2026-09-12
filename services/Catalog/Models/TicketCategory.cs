using System;

namespace Catalog.Service.Models;

public class TicketCategory
{
    public Guid Id { get; set; }
    public Guid ShowId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Capacity { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}
