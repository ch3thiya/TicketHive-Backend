namespace Booking.Service.Db;

public record ReminderOrder(Guid OrderId, int TicketCount);

public interface IReminderEligibilityRepository
{
    // Of the given orders, those that are still Confirmed and have at least one ticket that is not
    // voided, with that live ticket count. Cancelled, failed and unpaid orders are left out.
    Task<IReadOnlyList<ReminderOrder>> GetEligibleAsync(IReadOnlyCollection<Guid> orderIds);
}
