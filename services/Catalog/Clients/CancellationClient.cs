using System.Net.Http.Json;
using System.Text.Json;

namespace Catalog.Service.Clients;

public class CancellationClient(IHttpClientFactory factory)
{
    public async Task StopSalesAsync(Guid id)
    {
        using var response = await factory.CreateClient("CancellationInventory").PutAsync($"internal/inventory/cancellations/shows/{id}", null);
        response.EnsureSuccessStatusCode();
    }
    public async Task CancelOrdersAsync(Guid id)
    {
        using var response = await factory.CreateClient("CancellationBooking").PutAsync($"internal/booking/cancellations/shows/{id}", null);
        response.EnsureSuccessStatusCode();
    }
    public async Task<JsonElement> ProgressAsync(Guid id) =>
        await factory.CreateClient("CancellationBooking").GetFromJsonAsync<JsonElement>($"internal/booking/cancellations/shows/{id}/progress");
}
