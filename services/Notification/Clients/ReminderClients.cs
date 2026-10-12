using System.Net.Http.Json;
using Notification.Service.Models;

namespace Notification.Service.Clients;

public interface IReminderCatalogClient
{
    /// <summary>Live details of the given shows. Null means Catalog could not answer: send nothing. Shows that do not exist are absent.</summary>
    Task<IReadOnlyDictionary<Guid, ReminderShow>?> GetShowsAsync(IReadOnlyCollection<Guid> showIds, CancellationToken cancellationToken = default);
}

public interface IReminderBookingClient
{
    /// <summary>Order id to live ticket count for the orders still confirmed. Null means Booking could not answer: send nothing.</summary>
    Task<IReadOnlyDictionary<Guid, int>?> GetEligibleOrdersAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken = default);
}

public class ReminderCatalogClient(HttpClient http, ILogger<ReminderCatalogClient> logger) : IReminderCatalogClient
{
    private record ShowDto(Guid ShowId, DateTimeOffset StartsAtUtc, string EventName, string? VenueName, bool Cancelled);

    private record ResponseDto(List<ShowDto>? Shows);

    public async Task<IReadOnlyDictionary<Guid, ReminderShow>?> GetShowsAsync(IReadOnlyCollection<Guid> showIds, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("internal/catalog/shows/reminder-info", new { ShowIds = showIds }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Catalog reminder lookup failed with status {StatusCode}", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<ResponseDto>(cancellationToken);
            return body?.Shows?.ToDictionary(s => s.ShowId, s => new ReminderShow(s.ShowId, s.StartsAtUtc, s.EventName, s.VenueName, s.Cancelled));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Network errors, timeouts, unreadable bodies and the resilience pipeline's own exceptions.
            logger.LogWarning(ex, "Catalog reminder lookup failed");
            return null;
        }
    }
}

public class ReminderBookingClient(HttpClient http, ILogger<ReminderBookingClient> logger) : IReminderBookingClient
{
    private record OrderDto(Guid OrderId, int TicketCount);

    private record ResponseDto(List<OrderDto>? Orders);

    public async Task<IReadOnlyDictionary<Guid, int>?> GetEligibleOrdersAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("internal/booking/orders/reminder-eligibility", new { OrderIds = orderIds }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Booking reminder eligibility check failed with status {StatusCode}", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<ResponseDto>(cancellationToken);
            return body?.Orders?.ToDictionary(o => o.OrderId, o => o.TicketCount);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Booking reminder eligibility check failed");
            return null;
        }
    }
}
