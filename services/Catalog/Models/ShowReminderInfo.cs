namespace Catalog.Service.Models;

/// <summary>What Notification needs to remind customers of one show.</summary>
public record ShowReminderInfo(
    Guid ShowId,
    DateOnly ShowDate,
    TimeOnly ShowTime,
    string ShowStatus,
    string EventName,
    string EventStatus,
    string? VenueName)
{
    // Catalog stores local show time; all current venues are in Sri Lanka (ADR-022 cancellation rules).
    private static readonly TimeZoneInfo VenueZone =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Sri Lanka Standard Time" : "Asia/Colombo");

    public bool Cancelled => ShowStatus == "Cancelled" || EventStatus == "Cancelled";

    public DateTimeOffset StartsAtUtc
    {
        get
        {
            var local = ShowDate.ToDateTime(ShowTime, DateTimeKind.Unspecified);
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, VenueZone), TimeSpan.Zero);
        }
    }
}
