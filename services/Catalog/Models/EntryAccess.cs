namespace Catalog.Service.Models;

public enum EntryAccessOutcome
{
    Allowed,
    ShowNotFound,
    NotAnOrganizer,
    NotShowOwner,
    OrganizerStatusUnavailable
}

public record EntryAccessResult(Guid ShowId, EntryAccessOutcome Outcome)
{
    public bool IsAllowed => Outcome == EntryAccessOutcome.Allowed;
}