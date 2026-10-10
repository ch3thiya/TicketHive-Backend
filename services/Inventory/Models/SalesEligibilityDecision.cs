namespace Inventory.Service.Models;

public enum SalesEligibilityStatus
{
    Eligible,
    OrganizerSuspended,
    NotOnSale,
    Unavailable
}

/// <summary>
/// Catalog's answer to "may this show take new sales right now?". Anything Inventory cannot
/// verify is <see cref="SalesEligibilityStatus.Unavailable"/> and is treated as a refusal.
/// </summary>
public record SalesEligibilityDecision(SalesEligibilityStatus Status)
{
    public bool IsEligible => Status == SalesEligibilityStatus.Eligible;
}