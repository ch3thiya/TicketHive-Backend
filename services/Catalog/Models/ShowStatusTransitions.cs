using System.Collections.Generic;

namespace Catalog.Service.Models;

/// <summary>
/// The show lifecycle: Active -> Cancelled. No other transition is allowed,
/// and a Cancelled show can no longer be edited.
/// </summary>
public static class ShowStatusTransitions
{
    private static readonly Dictionary<string, HashSet<string>> AllowedTransitions = new()
    {
        ["Active"] = new HashSet<string> { "Cancelled" },
        ["Cancelled"] = new HashSet<string>(),
    };

    public static bool CanTransition(string fromStatus, string toStatus, out string? reason)
    {
        if (AllowedTransitions.TryGetValue(fromStatus, out var targets) && targets.Contains(toStatus))
        {
            reason = null;
            return true;
        }

        reason = $"Cannot change show status from '{fromStatus}' to '{toStatus}'.";
        return false;
    }

    public static bool CanEdit(string status, out string? reason)
    {
        if (status == "Cancelled")
        {
            reason = "Cannot edit a cancelled show.";
            return false;
        }

        reason = null;
        return true;
    }
}
