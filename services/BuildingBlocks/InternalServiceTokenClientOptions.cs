namespace BuildingBlocks;

public class InternalServiceTokenClientOptions
{
    public const string SectionName = "Wso2:InternalApi";

    public string TokenEndpoint { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
}
