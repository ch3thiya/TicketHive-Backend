namespace WaitingRoom.Service.Services;

public class AdmissionTokenOptions
{
    public const string SectionName = "AdmissionToken";

    public string PrivateKeyPem { get; set; } = string.Empty;

    public string Issuer { get; set; } = "tickethive-waiting-room";

    public int ExpiryMinutes { get; set; } = 15;
}
