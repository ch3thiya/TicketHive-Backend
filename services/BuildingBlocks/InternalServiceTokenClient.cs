using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuildingBlocks;

public class InternalServiceTokenClient : IInternalServiceTokenClient
{
    // Refresh a little before actual expiry so a request never races an
    // about-to-expire token past the resource server's own clock skew.
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly InternalServiceTokenClientOptions _options;
    private readonly ILogger<InternalServiceTokenClient> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private CachedToken? _cachedToken;

    public InternalServiceTokenClient(
        HttpClient httpClient,
        TimeProvider timeProvider,
        IOptions<InternalServiceTokenClientOptions> options,
        ILogger<InternalServiceTokenClient> logger)
    {
        _httpClient = httpClient;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var cached = _cachedToken;
        if (cached is not null && cached.ExpiresAt > _timeProvider.GetUtcNow())
        {
            return cached.AccessToken;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            cached = _cachedToken;
            if (cached is not null && cached.ExpiresAt > _timeProvider.GetUtcNow())
            {
                return cached.AccessToken;
            }

            var token = await FetchTokenAsync(cancellationToken);
            _cachedToken = token;
            return token.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<CachedToken> FetchTokenAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = _options.Scope
            })
        };

        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to reach the internal service token endpoint");
            throw new InvalidOperationException("Failed to reach the internal service token endpoint.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Internal service token request failed with status {StatusCode}", response.StatusCode);
            throw new InvalidOperationException($"Internal service token request failed with status {response.StatusCode}.");
        }

        TokenResponse? tokenResponse;
        try
        {
            tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Internal service token response could not be parsed");
            throw new InvalidOperationException("Internal service token response could not be parsed.", ex);
        }

        if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
        {
            throw new InvalidOperationException("Internal service token response did not contain an access token.");
        }

        var lifetime = TimeSpan.FromSeconds(Math.Max(tokenResponse.ExpiresIn, 0));
        var expiresAt = _timeProvider.GetUtcNow() + lifetime - ExpiryBuffer;

        return new CachedToken(tokenResponse.AccessToken, expiresAt);
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
