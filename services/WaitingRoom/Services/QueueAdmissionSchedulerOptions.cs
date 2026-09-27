namespace WaitingRoom.Service.Services;

// How often the scheduler polls, not how often a queue admits: each queue's
// admit_interval_seconds is enforced in SQL, so polling faster than it only
// cuts the drift that would otherwise make a tick land just short and skip
// a whole cycle.
public class QueueAdmissionSchedulerOptions
{
    public const string SectionName = "QueueAdmissionScheduler";

    public int TickIntervalSeconds { get; set; } = 5;
}
