namespace Catalog.Service.Models;

public enum SalesEligibilityOutcome
{
    Eligible,
    ShowNotFound,
    ShowNotOnSale,
    OrganizerSuspended,
    OrganizerNotActive,
    OrganizerStatusUnavailable
}

public record SalesEligibilityResult(Guid ShowId, SalesEligibilityOutcome Outcome)
{
    public bool IsEligible => Outcome == SalesEligibilityOutcome.Eligible;
}

/// <summary>
/// A show can take new sales only while it is Active inside a Published event. A draft or
/// cancelled show or event is never sellable, whatever the organizer's account status.
/// </summary>
public static class SalesEligibilityRules
{
    public static bool IsShowSellable(string eventStatus, string showStatus) =>
        eventStatus == "Published" && showStatus == "Active";
}