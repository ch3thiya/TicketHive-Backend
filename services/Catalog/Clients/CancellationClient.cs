using System.Net.Http.Json;
using System.Text.Json;

namespace Catalog.Service.Clients;

public class CancellationClient(IHttpClientFactory factory)
{
    public async Task EstablishCancellationBarrierAsync(Guid id)
    {
        using var inventory = await factory.CreateClient("CancellationInventory").PutAsync($"internal/inventory/cancellations/shows/{id}", null);
        inventory.EnsureSuccessStatusCode();

        // The public cancellation request is not acknowledged until Booking has
        // installed its own show tombstone as well. Inventory closing sales alone
        // leaves a window where a previously frozen hold can become a new order.
        using var booking = await factory.CreateClient("CancellationBooking").PutAsync($"internal/booking/cancellations/shows/{id}", null);
        booking.EnsureSuccessStatusCode();
    }
    public async Task<JsonElement> ProgressAsync(Guid id) =>
        await factory.CreateClient("CancellationBooking").GetFromJsonAsync<JsonElement>($"internal/booking/cancellations/shows/{id}/progress");
}
