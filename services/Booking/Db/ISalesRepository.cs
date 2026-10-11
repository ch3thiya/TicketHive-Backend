using Booking.Service.Models;

namespace Booking.Service.Db;

public interface ISalesRepository
{
    // Confirmed orders only: cancelled, failed and payment-pending orders never count. Read live.
    // Both lists come from one snapshot, so a sale or scan landing mid-read cannot make the used
    // tickets disagree with the sold ones.
    Task<(IReadOnlyList<SoldRow> Sold, IReadOnlyList<UsedRow> Used)> GetAsync(Guid showId);
}
