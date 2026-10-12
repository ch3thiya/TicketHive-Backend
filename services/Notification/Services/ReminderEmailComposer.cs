using System.Globalization;
using Notification.Service.Models;

namespace Notification.Service.Services;

public static class ReminderEmailComposer
{
    // Reminders show the time where the show happens; all current venues are in Sri Lanka.
    private static readonly TimeZoneInfo VenueZone =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Sri Lanka Standard Time" : "Asia/Colombo");

    public static ReminderEmail Compose(ReminderRow row, ReminderShow show, int ticketCount)
    {
        var local = TimeZoneInfo.ConvertTime(show.StartsAtUtc, VenueZone);
        var date = local.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture);
        var time = local.ToString("h:mm tt", CultureInfo.InvariantCulture);
        var when = $"{date} at {time} (Sri Lanka time)";
        var venue = string.IsNullOrWhiteSpace(show.VenueName) ? "Venue to be announced" : show.VenueName!;
        var tickets = ticketCount == 1 ? "1 ticket" : $"{ticketCount} tickets";
        var name = string.IsNullOrWhiteSpace(row.CustomerName) ? "there" : row.CustomerName;

        return new ReminderEmail(
            row.RecipientEmail,
            row.CustomerName,
            $"Reminder: {show.EventName} is coming up",
            $"Hi {name}, this is a reminder that {show.EventName} starts on {when}. Venue: {venue}. You have {tickets}. " +
            "Your tickets are in My Tickets on TicketHive. See you there!",
            show.EventName,
            when,
            venue,
            ticketCount);
    }
}
