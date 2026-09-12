namespace Catalog.Service.Clients;

public class OrganizerStatusClientOptions
{
    public const string SectionName = "Services:Identity";

    public string BaseUrl { get; set; } = string.Empty;

    public int CacheDurationSeconds { get; set; } = 60;
}
