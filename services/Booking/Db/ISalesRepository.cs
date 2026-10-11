using Booking.Service.Models;

namespace Booking.Service.Db;

public interface ISalesRepository
{
    // Confirmed orders only: cancelled, failed and payment-pending orders never count. Read live.
    Task<IReadOnlyList<SoldRow>> GetSoldAsync(Guid showId);

    Task<IReadOnlyList<UsedRow>> GetUsedAsync(Guid showId);
}
