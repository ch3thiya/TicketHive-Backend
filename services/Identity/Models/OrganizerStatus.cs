using System;

namespace Identity.Service.Models;

public enum OrganizerStatusAction
{
    Suspend,
    Reinstate
}

public enum OrganizerStatusChangeOutcome
{
    Changed,
    Unchanged,
    NotFound,
    InvalidTransition
}

public static class OrganizerStatuses
{
    public const string Approved = "approved";
    public const string Suspended = "suspended";

    public static bool IsKnownOrganizerStatus(string? status) => status is Approved or Suspended;
}

public record OrganizerStatusChangeResult(OrganizerStatusChangeOutcome Outcome, string? Status);

public record OrganizerStatusAuditEntry(
    Guid Id,
    Guid OrganizerId,
    string Action,
    string Reason,
    string ActorSub,
    DateTimeOffset OccurredAt);

public readonly record struct OrganizerStatusDecision(OrganizerStatusChangeOutcome Outcome, string? TargetStatus);

/// <summary>
/// Suspension state machine: only an approved organizer can be suspended and only a
/// suspended organizer can be reinstated. Asking for the state an organizer is already in
/// is a harmless repeat, not an error.
/// </summary>
public static class OrganizerStatusRules
{
    public static OrganizerStatusDecision Decide(string currentStatus, OrganizerStatusAction action)
    {
        return action switch
        {
            OrganizerStatusAction.Suspend when currentStatus == OrganizerStatuses.Suspended
                => new(OrganizerStatusChangeOutcome.Unchanged, OrganizerStatuses.Suspended),
            OrganizerStatusAction.Suspend when currentStatus == OrganizerStatuses.Approved
                => new(OrganizerStatusChangeOutcome.Changed, OrganizerStatuses.Suspended),
            OrganizerStatusAction.Reinstate when currentStatus == OrganizerStatuses.Approved
                => new(OrganizerStatusChangeOutcome.Unchanged, OrganizerStatuses.Approved),
            OrganizerStatusAction.Reinstate when currentStatus == OrganizerStatuses.Suspended
                => new(OrganizerStatusChangeOutcome.Changed, OrganizerStatuses.Approved),
            _ => new(OrganizerStatusChangeOutcome.InvalidTransition, null)
        };
    }
}
