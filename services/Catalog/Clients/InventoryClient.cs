using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Catalog.Service.Clients;

public class InventoryClient : IInventoryClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<InventoryClient> _logger;

    public InventoryClient(HttpClient httpClient, ILogger<InventoryClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task InitializeShowStockAsync(Guid showId, InitializeShowStockRequest request, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PutAsJsonAsync($"internal/inventory/shows/{showId}", request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Inventory initialization request failed for show {ShowId}", showId);
            throw new InventoryUnavailableException("Inventory is unavailable.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Inventory initialization request timed out for show {ShowId}", showId);
            throw new InventoryUnavailableException("Inventory is unavailable.", ex);
        }
        catch (InvalidOperationException ex)
        {
            // Thrown by the internal-token delegating handler when it fails
            // to acquire a machine-to-machine access token.
            _logger.LogWarning(ex, "Failed to acquire a service token for the Inventory call for show {ShowId}", showId);
            throw new InventoryUnavailableException("Inventory is unavailable.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Inventory initialization failed for show {ShowId} with status {StatusCode}", showId, response.StatusCode);
            throw new InventoryUnavailableException($"Inventory initialization failed with status {response.StatusCode}.");
        }
    }
}
