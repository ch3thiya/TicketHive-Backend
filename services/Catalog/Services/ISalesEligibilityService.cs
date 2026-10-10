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
}