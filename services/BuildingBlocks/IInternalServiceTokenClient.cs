namespace BuildingBlocks;

public interface IInternalServiceTokenClient
{
    /// <summary>
    /// Returns a client-credentials access token for calling another
    /// service's internal endpoints, fetching a new one only when none is
    /// cached or the cached one is close to expiry.
    /// </summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}
