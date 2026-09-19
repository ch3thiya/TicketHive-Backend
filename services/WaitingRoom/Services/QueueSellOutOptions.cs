namespace WaitingRoom.Service.Services;

public class QueueSellOutOptions
{
    public const string SectionName = "QueueSellOutCheck";

    public int CheckIntervalSeconds { get; set; } = 60;
}
