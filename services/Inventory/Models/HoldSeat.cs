namespace Inventory.Service.Models;

public record HoldSeat(Guid SeatId, Guid CategoryId, string Section, string Row, int Number);
