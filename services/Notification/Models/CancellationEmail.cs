namespace Notification.Service.Models;

public record CancellationEmail(string Key, string CustomerEmail, string CustomerName, Guid ShowId,
    Guid[] OrderIds, decimal Amount, string Currency, bool Simulated, string Reason);
