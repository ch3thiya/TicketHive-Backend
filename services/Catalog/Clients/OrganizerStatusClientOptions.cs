namespace Catalog.Service.Clients;

public class OrganizerStatusClientOptions
{
    public const string SectionName = "Services:Identity";

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// How long listing flags (<c>salesAvailable</c>) may lag behind Identity. Management and
    /// hold-time checks are never cached. Keep this small: it is the documented propagation
    /// boundary for customer-facing display only.
    /// </summary>
    public int ListingCacheSeconds { get; set; } = 5;
}