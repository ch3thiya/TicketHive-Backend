namespace Inventory.Service.Models;

// How often ExpiredHoldReleaseWorker sweeps for expired holds, and how many
// it claims per pass (ADR-008). Bound from configuration so both can be
// tuned for a show's expected on-sale volume without a code change.
public class HoldExpirySweepOptions
{
    public const string SectionName = "HoldExpiry:Sweep";

    public double IntervalSeconds { get; set; } = 10;
    public int BatchSize { get; set; } = 200;
}
