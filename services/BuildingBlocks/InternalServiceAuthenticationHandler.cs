using System.Net.Http.Headers;

namespace BuildingBlocks;

/// <summary>
/// Attaches a cached client-credentials bearer token to outgoing calls to
/// another service's internal endpoints. Add to a typed client with
/// AddHttpMessageHandler so the standard resilience handler still applies.
/// </summary>
public class InternalServiceAuthenticationHandler : DelegatingHandler
{
    private readonly IInternalServiceTokenClient _tokenClient;

    public InternalServiceAuthenticationHandler(IInternalServiceTokenClient tokenClient)
    {
        _tokenClient = tokenClient;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var accessToken = await _tokenClient.GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await base.SendAsync(request, cancellationToken);
    }
}
