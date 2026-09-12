namespace Catalog.Service.Clients;

public enum OrganizerLookupStatus
{
    Active,
    NotFound,
    Unavailable
}

public record OrganizerLookupResult(OrganizerLookupStatus Status, Guid? OrganizerId);

public interface IOrganizerStatusClient
{
    /// <summary>
    /// Asks Identity whether the given subject is an approved, active organizer.
    /// Answers are cached briefly; a failed or unreachable call to Identity is
    /// reported as <see cref="OrganizerLookupStatus.Unavailable"/>, never as
    /// <see cref="OrganizerLookupStatus.NotFound"/>.
    /// </summary>
    Task<OrganizerLookupResult> GetOrganizerStatusAsync(string sub, CancellationToken cancellationToken = default);
}
