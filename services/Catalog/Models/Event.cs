using System;

namespace Catalog.Service.Models;

public class Event
{
    public Guid Id { get; set; }
    public Guid OrganizerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DateOnly? EventDate { get; set; }
    public TimeOnly? EventTime { get; set; }
    public string BannerUrl { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft";
    public int? CancellationCutoffHours { get; set; }
    public DateTime CreatedAt { get; set; }
}
