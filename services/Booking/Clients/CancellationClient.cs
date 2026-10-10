using System.Net.Http.Json;

namespace Booking.Service.Clients;

public record ShowCancellationRules(DateTimeOffset StartsAt, string Status);
public record RefundOutcome(string Status);
public record CancellationEmail(string Key, string CustomerEmail, string CustomerName, Guid ShowId,
    Guid[] OrderIds, decimal Amount, string Currency, bool Simulated, string Reason);
public record EmailOutcome(string Status);

public class CancellationClient(IHttpClientFactory factory)
{
    public async Task<ShowCancellationRules> GetRulesAsync(Guid showId) =>
        await factory.CreateClient("CancellationCatalog").GetFromJsonAsync<ShowCancellationRules>($"internal/catalog/cancellations/shows/{showId}/rules")
        ?? throw new HttpRequestException("Show rules unavailable.");

    public async Task ReturnAsync(Guid holdId)
    {
        using var response = await factory.CreateClient("CancellationInventory").PutAsync($"internal/inventory/cancellations/holds/{holdId}", null);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> RefundAsync(Guid orderId, decimal amount, string currency)
    {
        using var response = await factory.CreateClient("CancellationPayment").PutAsJsonAsync($"internal/payment/refunds/{orderId}", new { Amount = amount, Currency = currency });
        response.EnsureSuccessStatusCode();
        var outcome = await response.Content.ReadFromJsonAsync<RefundOutcome>();
        if (outcome?.Status is not ("Simulated" or "Succeeded")) throw new HttpRequestException("Refund is not complete.");
        return outcome.Status;
    }

    public async Task<string> NotifyAsync(CancellationEmail email)
    {
        using var response = await factory.CreateClient("CancellationNotification").PutAsJsonAsync("internal/notification/cancellations", email);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EmailOutcome>())?.Status ?? "Pending";
    }
}
