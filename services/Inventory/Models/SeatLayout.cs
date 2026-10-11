namespace Inventory.Service.Models;

public record SeatToInitialize(Guid CategoryId, string Section, string Row, int Number);

public enum EnableSeatsOutcome
{
    Enabled,
    AlreadyEnabled,
    Locked,
    StockMissing,
    Mismatch,
    StaleVersion
}

public record EnableSeatsResult(EnableSeatsOutcome Outcome, string? Detail = null);

public record SeatLockState(bool Seated, bool Locked, int? Version);
