using System.Diagnostics.Metrics;

namespace Notification.Service.Services;

// ADR-019: the reminder job is invisible when it works and silent when it stops, so every outcome and
// every pass is counted. Backed by the Meter AddServiceDefaults() registers.
public class ReminderMetrics
{
    public Counter<long> Outcomes { get; }
    public Counter<long> Runs { get; }
    public Histogram<double> RunDurationMs { get; }

    public ReminderMetrics(Meter meter)
    {
        Outcomes = meter.CreateCounter<long>(
            "notification.reminders.outcomes",
            unit: "{reminder}",
            description: "Reminders by outcome: sent, simulated, failed, retry, needs_reconciliation, skipped_*, deferred, dependency_unavailable.");

        Runs = meter.CreateCounter<long>(
            "notification.reminders.runs",
            unit: "{run}",
            description: "Reminder job passes, tagged by whether they found work.");

        RunDurationMs = meter.CreateHistogram<double>(
            "notification.reminders.run_duration",
            unit: "ms",
            description: "Duration of one reminder pass, including passes that threw.");
    }

    public void Outcome(string outcome, long count = 1) =>
        Outcomes.Add(count, new KeyValuePair<string, object?>("outcome", outcome));
}
