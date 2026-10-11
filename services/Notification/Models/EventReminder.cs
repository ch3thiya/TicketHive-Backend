namespace Notification.Service.Models;

public static class ReminderStatuses
{
    public const string Pending = "Pending";
    public const string Sending = "Sending";
    public const string Sent = "Sent";
    public const string Simulated = "Simulated";
    public const string Failed = "Failed";
    public const string NeedsReconciliation = "NeedsReconciliation";
    public const string Skipped = "Skipped";
    public const string MissingRecipient = "MissingRecipient";
}

public static class ReminderSkipReasons
{
    public const string ShowNotFound = "ShowNotFound";
    public const string ShowCancelled = "ShowCancelled";
    public const string ShowStarted = "ShowStarted";
    public const string OrderNotEligible = "OrderNotEligible";
    public const string RecipientNotDeliverable = "RecipientNotDeliverable";
}

// What the Kafka listener learns when an order's tickets are issued.
public record ReminderSeed(Guid OrderId, Guid ShowId, string RecipientEmail, string CustomerName, int TicketCount);

public record ReminderRow(Guid OrderId, Guid ShowId, string RecipientEmail, string CustomerName, int TicketCount, int Attempts);

public record ReminderRecord(
    Guid OrderId,
    string Status,
    string? SkipReason,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    string? LastError,
    DateTimeOffset? SentAt);

// Live details of one show, from Catalog.
public record ReminderShow(Guid ShowId, DateTimeOffset StartsAtUtc, string EventName, string? VenueName, bool Cancelled);

public enum ReminderSendOutcome
{
    Sent,
    Simulated,
    // The provider answered and refused, or live delivery is not configured: nothing was sent.
    Rejected,
    // No answer (timeout, dropped connection): the email may or may not have gone out.
    Ambiguous
}

public record ReminderSendResult(ReminderSendOutcome Outcome, string? Error = null);

public record ReminderEmail(
    string RecipientEmail,
    string CustomerName,
    string Subject,
    string Message,
    string EventName,
    string When,
    string Venue,
    int TicketCount);
