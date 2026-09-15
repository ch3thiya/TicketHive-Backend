namespace Catalog.Service.Services;

// Placeholder defaults for the per-show rules Inventory needs at publish
// time, until S2-05 introduces real sales rules (on-sale scheduling,
// high-demand queues, per-show customer limits). Bound from configuration
// so S2-05 can replace the source of these values without touching
// EventService.
public class PublishDefaultsOptions
{
    public const string SectionName = "Publish:Defaults";

    public int MaxPerCustomer { get; set; } = 6;
    public int HoldMinutes { get; set; } = 10;
}
