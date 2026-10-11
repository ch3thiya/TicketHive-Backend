namespace Catalog.Service.Services;

// Enabling a layout turns a show into a seated show, which only sells once Inventory (seat holds),
// Booking (seats on tickets) and the web app (seat map) are all deployed (ADR-027). Until an operator
// has confirmed that, enabling is refused so no real show becomes unsellable. Requests and drafts
// are unaffected.
public class SeatMapOptions
{
    public const string SectionName = "SeatMaps";

    public bool AllowEnable { get; set; }
}
