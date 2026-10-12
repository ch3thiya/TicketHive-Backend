using System.Net;
using System.Net.Http.Json;
using Catalog.Service.Models;

namespace Catalog.Service.Clients;

public enum EnableSeatsResult
{
    Enabled,
    Locked,
    Invalid,
    Conflict,
    Unavailable
}

public record SeatLockInfo(bool Seated, bool Locked, int? Version);

public interface IInventorySeatClient
{
    /// <summary>Idempotent per show and version. Never throws for service failures; returns Unavailable.</summary>
    Task<EnableSeatsResult> EnableSeatsAsync(Guid showId, int version, IReadOnlyList<SeatPosition> seats, CancellationToken cancellationToken = default);

    /// <summary>Null means Inventory could not answer; callers must fail closed.</summary>
    Task<SeatLockInfo?> GetLockAsync(Guid showId, CancellationToken cancellationToken = default);
}

public class InventorySeatClient(HttpClient httpClient, ILogger<InventorySeatClient> logger) : IInventorySeatClient
{
    public async Task<EnableSeatsResult> EnableSeatsAsync(Guid showId, int version, IReadOnlyList<SeatPosition> seats, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.PutAsJsonAsync($"internal/inventory/shows/{showId}/seats", new { Version = version, Seats = seats }, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NoContent || response.IsSuccessStatusCode)
            {
                return EnableSeatsResult.Enabled;
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                return EnableSeatsResult.Invalid;
            }

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return body.Contains("Layout locked", StringComparison.Ordinal) ? EnableSeatsResult.Locked : EnableSeatsResult.Conflict;
            }

            logger.LogWarning("Inventory refused seats for show {ShowId} with status {StatusCode}", showId, (int)response.StatusCode);
            return EnableSeatsResult.Unavailable;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Inventory seat enablement failed for show {ShowId}", showId);
            return EnableSeatsResult.Unavailable;
        }
    }

    public async Task<SeatLockInfo?> GetLockAsync(Guid showId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync($"internal/inventory/shows/{showId}/seats/lock", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Inventory seat lock lookup for show {ShowId} returned {StatusCode}", showId, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<SeatLockInfo>(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Inventory seat lock lookup failed for show {ShowId}", showId);
            return null;
        }
    }
}
