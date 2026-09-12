using System.Collections.Generic;

namespace Catalog.Service.Models;

/// <summary>
/// The event lifecycle: Draft -> Published -> Cancelled, and Draft -> Cancelled.
/// No other transition is allowed, and a Cancelled event can no longer be edited.
/// </summary>
public static class EventStatusTransitions
{
    private static readonly Dictionary<string, HashSet<string>> AllowedTransitions = new()
    {
        ["Draft"] = new HashSet<string> { "Published", "Cancelled" },
        ["Published"] = new HashSet<string> { "Cancelled" },
        ["Cancelled"] = new HashSet<string>(),
    };

    public static bool CanTransition(string fromStatus, string toStatus, out string? reason)
    {
        if (AllowedTransitions.TryGetValue(fromStatus, out var targets) && targets.Contains(toStatus))
        {
            reason = null;
            return true;
        }

        reason = $"Cannot change event status from '{fromStatus}' to '{toStatus}'.";
        return false;
    }

    public static bool CanEdit(string status, out string? reason)
    {
        if (status == "Cancelled")
        {
            reason = "Cannot edit a cancelled event.";
            return false;
        }

        reason = null;
        return true;
    }
}
