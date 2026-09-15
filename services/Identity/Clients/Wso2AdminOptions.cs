namespace Identity.Service.Clients;

public class Wso2AdminOptions
{
    public const string SectionName = "Wso2";

    public string? AdminUsername { get; set; }
    public string? AdminPassword { get; set; }
    public string? M2mClientId { get; set; }
    public string? M2mClientSecret { get; set; }
}
