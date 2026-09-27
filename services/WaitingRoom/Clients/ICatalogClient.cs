namespace WaitingRoom.Service.Clients;

public record ShowSalesRules(Guid ShowId, DateTimeOffset? OnSaleAt, bool HighDemand);

public interface ICatalogClient
{
    /// <summary>
    /// Reads a show's sales rules from Catalog (short cache, timeout, and
    /// the resilience handler applied globally via AddServiceDefaults).
    /// Returns null when Catalog reports the show does not exist. Throws
    /// <see cref="CatalogUnavailableException"/> for any other failure.
    /// </summary>
    Task<ShowSalesRules?> GetSalesRulesAsync(Guid showId, CancellationToken cancellationToken = default);
}
