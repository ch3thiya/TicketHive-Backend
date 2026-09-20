namespace WaitingRoom.Service.Services;

public class QueueDefaultsOptions
{
    public const string SectionName = "QueueDefaults";

    public int AdmitBatch { get; set; } = 50;

    public int AdmitIntervalSeconds { get; set; } = 30;

    public int PrequeueWindowMinutes { get; set; } = 30;
}
