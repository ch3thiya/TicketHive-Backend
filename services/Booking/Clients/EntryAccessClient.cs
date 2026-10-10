using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Booking.Service.Clients;

public class EntryAccessClient : IEntryAccessClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<EntryAccessClient> _logger;

    public EntryAccessClient(HttpClient httpClient, ILogger<EntryAccessClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<EntryAccessDecision> CheckAsync(Guid showId, string sub, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                $"internal/catalog/shows/{showId}/entry-access?sub={Uri.EscapeDataString(sub)}", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // 404 (show unknown to Catalog), 401/403 (rejected service token) and 5xx are all
                // "cannot confirm", never "allowed".
                _logger.LogWarning("Catalog entry access check for show {ShowId} failed with status {StatusCode}", showId, response.StatusCode);
                return EntryAccessDecision.Unavailable;
            }

            var body = await response.Content.ReadFromJsonAsync<EntryAccessDto>(cancellationToken);
            if (body is null)
            {
                _logger.LogWarning("Catalog returned an empty entry access response for show {ShowId}", showId);
                return EntryAccessDecision.Unavailable;
            }

            return body.Allowed ? EntryAccessDecision.Allowed : EntryAccessDecision.Denied;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Includes the resilience pipeline's timeout and open-circuit exceptions.
            _logger.LogWarning(ex, "Catalog entry access check for show {ShowId} failed", showId);
            return EntryAccessDecision.Unavailable;
        }
    }

    private record EntryAccessDto(Guid ShowId, bool Allowed, string? Reason);
}