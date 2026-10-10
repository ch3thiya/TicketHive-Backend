using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Inventory.Service.Models;

namespace Inventory.Service.Clients;

public class SalesEligibilityClient : ISalesEligibilityClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SalesEligibilityClient> _logger;

    public SalesEligibilityClient(HttpClient httpClient, ILogger<SalesEligibilityClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<SalesEligibilityDecision> CheckAsync(Guid showId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"internal/catalog/shows/{showId}/sales-eligibility", cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new SalesEligibilityDecision(SalesEligibilityStatus.NotOnSale);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Catalog sales eligibility check for show {ShowId} failed with status {StatusCode}", showId, response.StatusCode);
                return new SalesEligibilityDecision(SalesEligibilityStatus.Unavailable);
            }

            var body = await response.Content.ReadFromJsonAsync<SalesEligibilityDto>(cancellationToken);
            if (body is null)
            {
                _logger.LogWarning("Catalog returned an empty sales eligibility response for show {ShowId}", showId);
                return new SalesEligibilityDecision(SalesEligibilityStatus.Unavailable);
            }

            if (body.Eligible)
            {
                return new SalesEligibilityDecision(SalesEligibilityStatus.Eligible);
            }

            return new SalesEligibilityDecision(
                string.Equals(body.Reason, "OrganizerSuspended", StringComparison.Ordinal)
                    ? SalesEligibilityStatus.OrganizerSuspended
                    : SalesEligibilityStatus.NotOnSale);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Catalog sales eligibility check for show {ShowId} failed", showId);
            return new SalesEligibilityDecision(SalesEligibilityStatus.Unavailable);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Catalog sales eligibility check for show {ShowId} timed out", showId);
            return new SalesEligibilityDecision(SalesEligibilityStatus.Unavailable);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogWarning(ex, "Catalog returned an unreadable sales eligibility response for show {ShowId}", showId);
            return new SalesEligibilityDecision(SalesEligibilityStatus.Unavailable);
        }
    }

    private record SalesEligibilityDto(Guid ShowId, bool Eligible, string? Reason);
}