namespace Notification.Service.Services;

public enum ShowReminderWindow
{
    // The show has not reached its reminder window yet; look again at OpensAt.
    NotYet,
    Due,
    // The show has started (or is starting right now): too late for a reminder.
    Started
}

public static class ReminderRules
{
    // Booking falls back to this address when it has none for the customer.
    public const string PlaceholderEmail = "customer@tickethive.lk";

    public static bool IsDeliverableRecipient(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 255 || email.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (string.Equals(email, PlaceholderEmail, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var at = email.IndexOf('@');
        return at > 0 && at == email.LastIndexOf('@') && at < email.Length - 1 && email.IndexOf('.', at) > at + 1 && !email.EndsWith('.');
    }

    // Anyone whose show starts within the window and has not started is due now, including a customer who
    // bought after the window opened.
    public static ShowReminderWindow Evaluate(DateTimeOffset startsAtUtc, DateTimeOffset now, TimeSpan window)
    {
        if (startsAtUtc <= now)
        {
            return ShowReminderWindow.Started;
        }

        return startsAtUtc - now <= window ? ShowReminderWindow.Due : ShowReminderWindow.NotYet;
    }

    // When to look at a not-yet-due show again: when its window opens, but never later than the cap
    // (a rescheduled show may move earlier) and never sooner than a minute.
    public static DateTimeOffset NextLook(DateTimeOffset startsAtUtc, DateTimeOffset now, TimeSpan window, TimeSpan maxDefer)
    {
        var opens = startsAtUtc - window;
        var capped = now + maxDefer;
        var next = opens < capped ? opens : capped;
        return next < now.AddMinutes(1) ? now.AddMinutes(1) : next;
    }
}
