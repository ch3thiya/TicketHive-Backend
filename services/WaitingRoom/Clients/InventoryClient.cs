using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace WaitingRoom.Service.Clients;

public class InventoryClient : IInventoryClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<InventoryClient> _logger;

    public InventoryClient(HttpClient httpClient, ILogger<InventoryClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<bool> IsSoldOutAsync(Guid showId, CancellationToken cancellationToken = default)
    {
        AvailabilityResponseDto? body;
        try
        {
            var response = await _httpClient.GetAsync($"api/inventory/shows/{showId}/availability", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Inventory availability check failed for show {ShowId} with status {StatusCode}", showId, response.StatusCode);
                throw new InventoryUnavailableException($"Inventory availability check failed with status {response.StatusCode}.");
            }

            body = await response.Content.ReadFromJsonAsync<AvailabilityResponseDto>(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Inventory availability check failed for show {ShowId}", showId);
            throw new InventoryUnavailableException("Inventory is unavailable.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Inventory availability check timed out for show {ShowId}", showId);
            throw new InventoryUnavailableException("Inventory is unavailable.", ex);
        }

        if (body is null || body.Categories.Count == 0)
        {
            _logger.LogWarning("Inventory returned an empty availability response for show {ShowId}", showId);
            throw new InventoryUnavailableException("Inventory is unavailable.");
        }

        return body.Categories.All(c => c.Available <= 0);
    }

    private record AvailabilityResponseDto(Guid ShowId, List<StockItemDto> Categories);

    private record StockItemDto(Guid CategoryId, int Capacity, int Available, decimal UnitPrice, string Currency);
}
