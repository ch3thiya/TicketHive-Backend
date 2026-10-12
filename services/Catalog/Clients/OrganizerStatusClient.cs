using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Catalog.Service.Clients;

public class OrganizerStatusClient : IOrganizerStatusClient
{
    private const string ListingCacheKeyPrefix = "organizer-listing-status:";

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OrganizerStatusClient> _logger;
    private readonly TimeSpan _listingCacheDuration;

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
        _listingCacheDuration = TimeSpan.FromSeconds(Math.Max(0, options.Value.ListingCacheSeconds));
    }

    public Task<OrganizerLookupResult> GetOrganizerStatusAsync(string sub, CancellationToken cancellationToken = default) =>
        LookupAsync($"internal/identity/organizers/{Uri.EscapeDataString(sub)}", cancellationToken);

    public Task<OrganizerLookupResult> GetOrganizerStatusByIdAsync(Guid organizerId, CancellationToken cancellationToken = default) =>
        LookupAsync($"internal/identity/organizers/by-id/{organizerId}", cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, OrganizerLookupStatus>> GetOrganizerStatusesAsync(
        IReadOnlyCollection<Guid> organizerIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, OrganizerLookupStatus>();
        var now = _timeProvider.GetUtcNow();
        var missing = new List<Guid>();

        foreach (var id in organizerIds.Distinct())
        {
            if (_listingCacheDuration > TimeSpan.Zero
                && _cache.TryGetValue(ListingCacheKeyPrefix + id, out CacheEntry? cached)
                && cached is not null
                && cached.ExpiresAt > now)
            {
                result[id] = cached.Status;
            }
            else
            {
                missing.Add(id);
            }
        }

        if (missing.Count == 0)
        {
            return result;
        }

        var fetched = await FetchBatchAsync(missing, cancellationToken);
        foreach (var id in missing)
        {
            var status = fetched is null
                ? OrganizerLookupStatus.Unavailable
                : fetched.GetValueOrDefault(id, OrganizerLookupStatus.NotFound);
            result[id] = status;

            // An outage is cached for the same short window so a struggling Identity cannot
            // slow every public listing request. This is display data only.
            if (_listingCacheDuration > TimeSpan.Zero)
            {
                _cache.Set(ListingCacheKeyPrefix + id, new CacheEntry(status, now + _listingCacheDuration), _listingCacheDuration * 10);
            }
        }

        return result;
    }

    private async Task<Dictionary<Guid, OrganizerLookupStatus>?> FetchBatchAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync("internal/identity/organizers/status", new { organizerIds = ids }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Identity organizer batch lookup failed with status {StatusCode}", response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<List<OrganizerLookupResponseDto>>(cancellationToken);
            return body?.ToDictionary(b => b.OrganizerId, b => MapStatus(b.Status));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Includes the resilience pipeline's timeout and open-circuit exceptions.
            _logger.LogWarning(ex, "Identity organizer batch lookup failed");
            return null;
        }
    }

    private async Task<OrganizerLookupResult> LookupAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(path, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new OrganizerLookupResult(OrganizerLookupStatus.NotFound, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Identity organizer lookup failed with status {StatusCode}", response.StatusCode);
                return new OrganizerLookupResult(OrganizerLookupStatus.Unavailable, null);
            }

            var body = await response.Content.ReadFromJsonAsync<OrganizerLookupResponseDto>(cancellationToken);
            if (body is null || body.OrganizerId == Guid.Empty)
            {
                _logger.LogWarning("Identity returned an empty organizer lookup response");
                return new OrganizerLookupResult(OrganizerLookupStatus.Unavailable, null);
            }

            var status = MapStatus(body.Status);
            return new OrganizerLookupResult(status, status == OrganizerLookupStatus.Unavailable ? null : body.OrganizerId);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Includes the resilience pipeline's timeout and open-circuit exceptions, which are
            // not HttpRequestException. An outage is Unavailable, never NotFound or Active.
            _logger.LogWarning(ex, "Identity organizer lookup failed");
            return new OrganizerLookupResult(OrganizerLookupStatus.Unavailable, null);
        }
    }

    // Fail closed: only the two documented statuses are understood. A missing or unknown
    // value (for example an older Identity build that returns no status) is never treated
    // as an active organizer.
    private OrganizerLookupStatus MapStatus(string? status)
    {
        if (string.Equals(status, "approved", StringComparison.OrdinalIgnoreCase))
        {
            return OrganizerLookupStatus.Active;
        }

        if (string.Equals(status, "suspended", StringComparison.OrdinalIgnoreCase))
        {
            return OrganizerLookupStatus.Suspended;
        }

        _logger.LogWarning("Identity returned an unrecognised organizer status; treating the lookup as unavailable");
        return OrganizerLookupStatus.Unavailable;
    }

    private record OrganizerLookupResponseDto(Guid OrganizerId, string? Status);

    private record CacheEntry(OrganizerLookupStatus Status, DateTimeOffset ExpiresAt);
}