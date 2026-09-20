namespace Inventory.Service.Services;

public class AdmissionTokenOptions
{
    public const string SectionName = "AdmissionToken";

    public string PublicKeyPem { get; set; } = string.Empty;

    // Matches WaitingRoom.Service.Services.AdmissionTokenOptions.Issuer's
    // default — the two services must agree on this value without either
    // one calling the other.
    public string Issuer { get; set; } = "tickethive-waiting-room";
}
