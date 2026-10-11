namespace Inventory.Service.Models;

public record SeatMapSeat(Guid Id, Guid CategoryId, string Section, string Row, int Number, string State);

public record SeatMapCategory(Guid CategoryId, decimal UnitPrice, string Currency);

public record SeatMapView(Guid ShowId, int Version, IReadOnlyList<SeatMapCategory> Categories, IReadOnlyList<SeatMapSeat> Seats);

public record SeatState(Guid Id, string State);
