namespace Notification.Service.Services;

public class ReminderOptions
{
    public const string SectionName = "Reminders";

    // The job runs only when its dependencies are configured; this is an operator off-switch.
    public bool Enabled { get; set; } = true;

    // True records "Simulated" and never calls the email provider. Live sending is an explicit choice.
    public bool Simulate { get; set; } = true;

    // A reminder goes out once the show is due to start within this many hours (Product Owner to confirm).
    public int WindowHours { get; set; } = 24;

    public int PollSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 50;

    // Definite provider refusals are retried this many times in all, never an ambiguous send.
    public int MaxAttempts { get; set; } = 3;
    public int RetryMinutes { get; set; } = 10;

    // A send still marked "Sending" after this long has an unknown outcome and is not retried.
    public int StaleSendMinutes { get; set; } = 5;

    // Shows far in the future are looked at again at least this often, in case they were rescheduled.
    public int MaxDeferHours { get; set; } = 6;

    public string ServiceId { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;
}
