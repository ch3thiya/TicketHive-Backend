using Inventory.Service.Models;

namespace Inventory.Service.Db;

public interface ISeatLayoutRepository
{
    // Switches a show to seated stock and creates its seats in one transaction. The show's stock
    // rows are locked first, so a concurrent hold either committed before (and locks the layout)
    // or waits until this commits (and then cannot allocate GA stock). A repeat call for the same
    // version changes nothing.
    Task<EnableSeatsResult> EnableAsync(Guid showId, int version, IReadOnlyList<SeatToInitialize> seats, DateTimeOffset now);

    Task<SeatLockState> GetLockStateAsync(Guid showId);

    // Null when the show has no enabled layout. Always read live, never cached.
    Task<SeatMapView?> GetSeatMapAsync(Guid showId);

    // The seats that are not Available; null when the show has no enabled layout.
    Task<IReadOnlyList<SeatState>?> GetUnavailableSeatsAsync(Guid showId);
}
