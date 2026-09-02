using System;

namespace Catalog.Service.Models;

public class Show
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public DateOnly ShowDate { get; set; }
    public TimeOnly ShowTime { get; set; }
    public Guid? VenueId { get; set; }
    public DateTime? OnSaleAt { get; set; }
    public int? HighDemandThreshold { get; set; }
    public int? ReminderMinutesBefore { get; set; }
    public string Status { get; set; } = "Active";
    public DateTime CreatedAt { get; set; }
}
