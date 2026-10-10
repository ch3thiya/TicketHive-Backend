namespace Booking.Service.Models;

public static class CancellationRules
{
    public static string? Refusal(OrderStatus status, bool ownsOrder, bool hasUsedTicket,
        DateTimeOffset startsAt, DateTimeOffset now, bool automatic)
    {
        if (!automatic && !ownsOrder) return "NotFound";
        if (status == OrderStatus.Cancelled) return null;
        if (automatic) return null;
        if (status != OrderStatus.Confirmed) return "OrderNotConfirmed";
        if (hasUsedTicket) return "TicketUsed";
        if (now >= startsAt) return "ShowStarted";
        return null;
    }
}

public record CancellationState(Guid OrderId, string RefundStatus, bool InventoryReturned,
    string NotificationStatus, string Reason);
