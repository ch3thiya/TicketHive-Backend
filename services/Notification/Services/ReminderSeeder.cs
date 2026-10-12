using System.Text.Json;
using Notification.Service.Db;
using Notification.Service.Models;

namespace Notification.Service.Services;

/// <summary>
/// Records an order for its event reminder when its tickets are issued. Orders confirmed before this
/// existed are not reminded: there is deliberately no backfill.
/// </summary>
public class ReminderSeeder(EventReminderRepository reminders, ILogger<ReminderSeeder> logger)
{
    public async Task<bool> SeedAsync(JsonElement ticketsIssued, Guid orderId, string rawEmail, string customerName)
    {
        if (!ticketsIssued.TryGetProperty("ShowId", out var showProp)
            || showProp.ValueKind != JsonValueKind.String
            || !Guid.TryParse(showProp.GetString(), out var showId)
            || showId == Guid.Empty)
        {
            logger.LogWarning("Order {OrderId} has no show id in its tickets-issued event, so it cannot be reminded", orderId);
            return false;
        }

        var ticketCount = 0;
        if (ticketsIssued.TryGetProperty("TicketCount", out var countProp) && countProp.ValueKind == JsonValueKind.Number)
        {
            countProp.TryGetInt32(out ticketCount);
        }

        if (ticketCount <= 0 && ticketsIssued.TryGetProperty("TicketCodes", out var codes) && codes.ValueKind == JsonValueKind.Array)
        {
            ticketCount = codes.GetArrayLength();
        }

        var deliverable = ReminderRules.IsDeliverableRecipient(rawEmail);
        var created = await reminders.EnsureAsync(new ReminderSeed(orderId, showId, rawEmail ?? string.Empty, customerName, Math.Max(1, ticketCount)), deliverable);
        if (created)
        {
            logger.LogInformation("Order {OrderId} recorded for an event reminder ({Status})", orderId, deliverable ? "pending" : "no deliverable address");
        }

        return created;
    }
}
