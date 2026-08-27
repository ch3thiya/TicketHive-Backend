using System;

namespace Catalog.Service.Models;

public class Show
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public DateOnly ShowDate { get; set; }
    public TimeOnly ShowTime { get; set; }
    public DateTime CreatedAt { get; set; }
}
