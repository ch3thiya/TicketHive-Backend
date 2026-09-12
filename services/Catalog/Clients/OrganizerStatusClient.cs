using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Catalog.Service.Clients;

public class OrganizerStatusClient : IOrganizerStatusClient
{
    private const string CacheKeyPrefix = "organizer-status:";

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OrganizerStatusClient> _logger;
    private readonly TimeSpan _cacheDuration;

    public OrganizerStatusClient(
        HttpClient httpClient,
        IMemoryCache cache,
        TimeProvider timeProvider,
        IOptions<OrganizerStatusClientOptions> options,
        ILogger<OrganizerStatusClient> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _timeProvider = timeProvider;
        _logger = logger;
        _cacheDuration = TimeSpan.FromSeconds(options.Value.CacheDurationSeconds);
    }

    public async Task<OrganizerLookupResult> GetOrganizerStatusAsync(string sub, CancellationToken cancellationToken = default)
    {
        var cacheKey = CacheKeyPrefix + sub;
        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(cacheKey, out CacheEntry? cached) && cached is not null && cached.ExpiresAt > now)
        {
            return cached.Result;
        }

        OrganizerLookupResult result;
        try
        {
            var response = await _httpClient.GetAsync($"internal/identity/organizers/{Uri.EscapeDataString(sub)}", cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                result = new OrganizerLookupResult(OrganizerLookupStatus.NotFound, null);
            }
            else if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<OrganizerLookupResponseDto>(cancellationToken);
                if (body is null)
                {
                    _logger.LogWarning("Identity returned an empty organizer lookup response");
                    return new OrganizerLookupResult(OrganizerLookupStatus.Unavailable, null);
                }

                result = new OrganizerLookupResult(OrganizerLookupStatus.Active, body.OrganizerId);
            }
            else
            {
                _logger.LogWarning("Identity organizer lookup failed with status {StatusCode}", response.StatusCode);
                return new OrganizerLookupResult(OrganizerLookupStatus.Unavailable, null);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Identity organizer lookup failed");
            return new OrganizerLookupResult(OrganizerLookupStatus.Unavailable, null);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Identity organizer lookup timed out");
            return new OrganizerLookupResult(OrganizerLookupStatus.Unavailable, null);
        }

        // A generous memory-only expiration bounds cache growth; the freshness
        // window that matters for correctness is CacheEntry.ExpiresAt above,
        // checked against the injected TimeProvider so it stays testable.
        _cache.Set(cacheKey, new CacheEntry(result, now + _cacheDuration), _cacheDuration * 10);
        return result;
    }

    private record OrganizerLookupResponseDto(Guid OrganizerId);

    private record CacheEntry(OrganizerLookupResult Result, DateTimeOffset ExpiresAt);
}
