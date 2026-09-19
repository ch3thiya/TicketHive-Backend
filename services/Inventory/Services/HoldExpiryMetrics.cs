using System.Diagnostics.Metrics;

namespace Inventory.Service.Services;

// ADR-019: the sweeper is invisible when it works and catastrophic when it
// silently stops, as BRIEF #17 demonstrated. Backed by the Meter
// AddServiceDefaults() registers.
public class HoldExpiryMetrics
{
    public Counter<long> HoldsExpired { get; }
    public Counter<long> TicketsReturned { get; }
    public Histogram<double> SweepDurationMs { get; }

    public HoldExpiryMetrics(Meter meter)
    {
        HoldsExpired = meter.CreateCounter<long>(
            "inventory.hold_expiry.holds_released",
            unit: "{hold}",
            description: "Holds moved from Active to Expired by the expiry sweeper.");

        TicketsReturned = meter.CreateCounter<long>(
            "inventory.hold_expiry.tickets_returned",
            unit: "{ticket}",
            description: "Ticket quantity returned to stock by the expiry sweeper.");

        SweepDurationMs = meter.CreateHistogram<double>(
            "inventory.hold_expiry.sweep_duration",
            unit: "ms",
            description: "Duration of one expiry sweep pass, including passes that threw.");
    }
}
