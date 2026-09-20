using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Booking.Service.Clients;

public class InventoryClient : IInventoryClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<InventoryClient> _logger;

    public InventoryClient(HttpClient httpClient, ILogger<InventoryClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<InventoryHoldResponse?> GetHoldAsync(Guid holdId)
    {
        try
        {
            var response = await _httpClient.GetAsync($"internal/inventory/holds/{holdId}");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<InventoryHoldResponse>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch hold {HoldId} from Inventory Service", holdId);
            throw;
        }
    }

    public async Task<bool> FreezeHoldAsync(Guid holdId)
    {
        try
        {
            var response = await _httpClient.PatchAsync($"internal/inventory/holds/{holdId}/freeze", null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to freeze hold {HoldId} in Inventory Service", holdId);
            return false;
        }
    }

    public async Task<bool> ConvertHoldAsync(Guid holdId)
    {
        try
        {
            var response = await _httpClient.PatchAsync($"internal/inventory/holds/{holdId}/convert", null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to convert hold {HoldId} in Inventory Service", holdId);
            return false;
        }
    }

    public async Task<bool> ReleaseHoldAsync(Guid holdId)
    {
        try
        {
            var response = await _httpClient.PatchAsync($"internal/inventory/holds/{holdId}/release", null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to release hold {HoldId} in Inventory Service", holdId);
            return false;
        }
    }
}
