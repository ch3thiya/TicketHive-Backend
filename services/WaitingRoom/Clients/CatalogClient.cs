using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WaitingRoom.Service.Clients;

public class CatalogClient : ICatalogClient
{
    private const string CacheKeyPrefix = "sales-rules:";

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CatalogClient> _logger;
    private readonly TimeSpan _cacheDuration;

    public CatalogClient(
        HttpClient httpClient,
        IMemoryCache cache,
        TimeProvider timeProvider,
        IOptions<CatalogClientOptions> options,
        ILogger<CatalogClient> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _timeProvider = timeProvider;
        _logger = logger;
        _cacheDuration = TimeSpan.FromSeconds(options.Value.CacheDurationSeconds);
    }

    public async Task<ShowSalesRules?> GetSalesRulesAsync(Guid showId, CancellationToken cancellationToken = default)
    {
        var cacheKey = CacheKeyPrefix + showId;
        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(cacheKey, out CacheEntry? cached) && cached is not null && cached.ExpiresAt > now)
        {
            return cached.Result;
        }

        ShowSalesRules? result;
        try
        {
            var response = await _httpClient.GetAsync($"internal/catalog/shows/{showId}/sales-rules", cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                result = null;
            }
            else if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<ShowSalesRulesResponseDto>(cancellationToken);
                if (body is null)
                {
                    _logger.LogWarning("Catalog returned an empty sales rules response for show {ShowId}", showId);
                    throw new CatalogUnavailableException("Catalog is unavailable.");
                }

                result = new ShowSalesRules(body.ShowId, body.OnSaleAt, body.HighDemand);
            }
            else
            {
                _logger.LogWarning("Catalog sales rules lookup failed for show {ShowId} with status {StatusCode}", showId, response.StatusCode);
                throw new CatalogUnavailableException($"Catalog sales rules lookup failed with status {response.StatusCode}.");
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Catalog sales rules lookup failed for show {ShowId}", showId);
            throw new CatalogUnavailableException("Catalog is unavailable.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Catalog sales rules lookup timed out for show {ShowId}", showId);
            throw new CatalogUnavailableException("Catalog is unavailable.", ex);
        }
        catch (InvalidOperationException ex)
        {
            // Thrown by the internal-token delegating handler when it fails
            // to acquire a machine-to-machine access token.
            _logger.LogWarning(ex, "Failed to acquire a service token for the Catalog call for show {ShowId}", showId);
            throw new CatalogUnavailableException("Catalog is unavailable.", ex);
        }

        // A generous memory-only expiration bounds cache growth; the
        // freshness window that matters for correctness is CacheEntry.ExpiresAt
        // above, checked against the injected TimeProvider so it stays testable.
        _cache.Set(cacheKey, new CacheEntry(result, now + _cacheDuration), _cacheDuration * 10);
        return result;
    }

    private record ShowSalesRulesResponseDto(Guid ShowId, DateTimeOffset? OnSaleAt, bool HighDemand);

    private record CacheEntry(ShowSalesRules? Result, DateTimeOffset ExpiresAt);
}
