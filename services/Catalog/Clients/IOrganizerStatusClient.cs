namespace Catalog.Service.Clients;

public enum OrganizerLookupStatus
{
    Active,
    Suspended,
    NotFound,
    Unavailable
}

public record OrganizerLookupResult(OrganizerLookupStatus Status, Guid? OrganizerId);

public interface IOrganizerStatusClient
{
    /// <summary>
    /// Asks Identity for the authoritative status of the organizer with the given subject.
    /// Never cached, so a suspension or reinstatement applies to the next request. A failed,
    /// unreachable or unrecognised answer is <see cref="OrganizerLookupStatus.Unavailable"/>,
    /// never <see cref="OrganizerLookupStatus.NotFound"/> or Active.
    /// </summary>
    Task<OrganizerLookupResult> GetOrganizerStatusAsync(string sub, CancellationToken cancellationToken = default);

    /// <summary>Same as <see cref="GetOrganizerStatusAsync"/> but by organizer ID. Never cached.</summary>
    Task<OrganizerLookupResult> GetOrganizerStatusByIdAsync(Guid organizerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up many organizers for display (listings). Answers are cached for a few seconds
    /// (<see cref="OrganizerStatusClientOptions.ListingCacheSeconds"/>); never use this to
    /// authorize a write or a sale. Organizers whose status could not be determined map to
    /// <see cref="OrganizerLookupStatus.Unavailable"/>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, OrganizerLookupStatus>> GetOrganizerStatusesAsync(
        IReadOnlyCollection<Guid> organizerIds, CancellationToken cancellationToken = default);
}