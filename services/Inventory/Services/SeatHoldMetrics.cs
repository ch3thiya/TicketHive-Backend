using System.Diagnostics.Metrics;

namespace Inventory.Service.Services;

// ADR-019/027: the seat-hold path. Backed by the Meter AddServiceDefaults() registers.
public class SeatHoldMetrics
{
    public Counter<long> SeatsHeld { get; }
    public Counter<long> SelectionsRefused { get; }

    public SeatHoldMetrics(Meter meter)
    {
        SeatsHeld = meter.CreateCounter<long>(
            "inventory.seat_holds.seats_held",
            unit: "{seat}",
            description: "Seats held by new seat holds.");

        SelectionsRefused = meter.CreateCounter<long>(
            "inventory.seat_holds.selections_refused",
            unit: "{selection}",
            description: "Seat selections refused because a seat was taken, quota or stock ran out.");
    }
}
