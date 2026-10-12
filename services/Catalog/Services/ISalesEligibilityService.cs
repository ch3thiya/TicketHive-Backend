using Catalog.Service.Models;

namespace Catalog.Service.Services;

public interface ISalesEligibilityService
{
    /// <summary>
    /// Decides whether the show can take new sales right now: the show and event must be
    /// sellable and the owning organizer must be active. The organizer check is always made
    /// against Identity, never from a cache, and fails closed when Identity cannot answer.
    /// </summary>
    Task<SalesEligibilityResult> CheckAsync(Guid showId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns which of the given organizers are suspended, for customer-facing display only.
    /// Uses a short cache (see <c>OrganizerStatusClientOptions.ListingCacheSeconds</c>), so it can
    /// lag a status change by a few seconds; the hold-time check remains authoritative. Organizers
    /// whose status cannot be determined are not reported as suspended.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetSuspendedOrganizerIdsAsync(IEnumerable<Guid> organizerIds, CancellationToken cancellationToken = default);
}