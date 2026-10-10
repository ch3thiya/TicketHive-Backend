using Inventory.Service.Models;

namespace Inventory.Service.Clients;

public interface ISalesEligibilityClient
{
    /// <summary>
    /// Asks Catalog whether the show can take new sales. Never cached and never throws for
    /// transport failures: an unreachable, slow or misbehaving Catalog yields
    /// <see cref="SalesEligibilityStatus.Unavailable"/> so callers fail closed.
    /// </summary>
    Task<SalesEligibilityDecision> CheckAsync(Guid showId, CancellationToken cancellationToken = default);
}