namespace WaitingRoom.Service.Clients;

public class CatalogClientOptions
{
    public const string SectionName = "Services:Catalog";

    public string BaseUrl { get; set; } = string.Empty;

    public int CacheDurationSeconds { get; set; } = 30;
}
