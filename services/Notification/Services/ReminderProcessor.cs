using System.Diagnostics;
using Microsoft.Extensions.Options;
using Notification.Service.Clients;
using Notification.Service.Db;
using Notification.Service.Models;

namespace Notification.Service.Services;

/// <summary>
/// One pass of the reminder job. It claims due reminders, checks the live show (Catalog) and the live
/// order (Booking), and only then sends. If either service cannot answer nothing is sent and the
/// reminders wait for the next pass. A send that fails never touches orders or tickets.
/// </summary>
public class ReminderProcessor(
    EventReminderRepository reminders,
    IReminderCatalogClient catalog,
    IReminderBookingClient booking,
    IReminderEmailSender sender,
    IOptions<ReminderOptions> options,
    TimeProvider clock,
    ReminderMetrics metrics,
    ILogger<ReminderProcessor> logger)
{
    private static readonly TimeSpan DependencyRetry = TimeSpan.FromMinutes(1);

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var started = Stopwatch.GetTimestamp();
        var open = new HashSet<Guid>();
        try
        {
            var claimed = await reminders.ClaimDueAsync(settings.BatchSize, settings.StaleSendMinutes);
            metrics.Runs.Add(1, new KeyValuePair<string, object?>("work", claimed.Count > 0));
            if (claimed.Count == 0)
            {
                return 0;
            }

            foreach (var row in claimed) open.Add(row.OrderId);
            await ProcessAsync(claimed, open, cancellationToken);
            return claimed.Count;
        }
        finally
        {
            // Anything still claimed (the host is stopping, or a dependency call threw) goes back, unsent.
            if (open.Count > 0)
            {
                await reminders.DeferAsync(open, clock.GetUtcNow() + DependencyRetry);
            }

            metrics.RunDurationMs.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private async Task ProcessAsync(IReadOnlyList<ReminderRow> claimed, HashSet<Guid> open, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow();
        var window = TimeSpan.FromHours(settings.WindowHours);

        var shows = await catalog.GetShowsAsync(claimed.Select(r => r.ShowId).Distinct().ToList(), cancellationToken);
        if (shows is null)
        {
            metrics.Outcome("dependency_unavailable", claimed.Count);
            logger.LogWarning("Catalog could not be reached; {Count} reminder(s) wait for the next pass", claimed.Count);
            return;
        }

        var candidates = new List<(ReminderRow Row, ReminderShow Show)>();
        foreach (var byShow in claimed.GroupBy(r => r.ShowId))
        {
            if (!shows.TryGetValue(byShow.Key, out var show))
            {
                foreach (var row in byShow) await SkipAsync(row, ReminderSkipReasons.ShowNotFound, open);
                continue;
            }

            if (show.Cancelled)
            {
                foreach (var row in byShow) await SkipAsync(row, ReminderSkipReasons.ShowCancelled, open);
                continue;
            }

            switch (ReminderRules.Evaluate(show.StartsAtUtc, now, window))
            {
                case ShowReminderWindow.Started:
                    foreach (var row in byShow) await SkipAsync(row, ReminderSkipReasons.ShowStarted, open);
                    break;
                case ShowReminderWindow.NotYet:
                    var ids = byShow.Select(r => r.OrderId).ToList();
                    await reminders.DeferAsync(ids, ReminderRules.NextLook(show.StartsAtUtc, now, window, TimeSpan.FromHours(settings.MaxDeferHours)));
                    foreach (var id in ids) open.Remove(id);
                    metrics.Outcome("deferred", ids.Count);
                    break;
                default:
                    foreach (var row in byShow)
                    {
                        if (ReminderRules.IsDeliverableRecipient(row.RecipientEmail))
                        {
                            candidates.Add((row, show));
                        }
                        else
                        {
                            await SkipAsync(row, ReminderSkipReasons.RecipientNotDeliverable, open);
                        }
                    }

                    break;
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        var eligible = await booking.GetEligibleOrdersAsync(candidates.Select(c => c.Row.OrderId).ToList(), cancellationToken);
        if (eligible is null)
        {
            metrics.Outcome("dependency_unavailable", candidates.Count);
            logger.LogWarning("Booking could not be reached; {Count} reminder(s) wait for the next pass", candidates.Count);
            return;
        }

        foreach (var (row, show) in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!eligible.TryGetValue(row.OrderId, out var ticketCount))
            {
                await SkipAsync(row, ReminderSkipReasons.OrderNotEligible, open);
                continue;
            }

            await SendAsync(row, show, ticketCount, open, cancellationToken);
        }
    }

    private async Task SkipAsync(ReminderRow row, string reason, HashSet<Guid> open)
    {
        await reminders.SkipAsync(row.OrderId, reason);
        open.Remove(row.OrderId);
        metrics.Outcome("skipped_" + ToSnake(reason));
        logger.LogInformation("Reminder for order {OrderId} skipped: {Reason}", row.OrderId, reason);
    }

    private async Task SendAsync(ReminderRow row, ReminderShow show, int ticketCount, HashSet<Guid> open, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        ReminderSendResult result;
        try
        {
            result = await sender.SendAsync(ReminderEmailComposer.Compose(row, show, ticketCount), cancellationToken);
        }
        catch (Exception ex)
        {
            // Unknown whether anything went out, so it is never retried by itself.
            logger.LogWarning(ex, "Reminder delivery for order {OrderId} threw", row.OrderId);
            result = new ReminderSendResult(ReminderSendOutcome.Ambiguous, $"The send threw ({ex.GetType().Name}).");
        }

        switch (result.Outcome)
        {
            case ReminderSendOutcome.Sent:
            case ReminderSendOutcome.Simulated:
                var status = result.Outcome == ReminderSendOutcome.Sent ? ReminderStatuses.Sent : ReminderStatuses.Simulated;
                await reminders.CompleteAsync(row.OrderId, status);
                metrics.Outcome(status.ToLowerInvariant());
                logger.LogInformation("Reminder for order {OrderId} {Status} ({Tickets} ticket(s))", row.OrderId, status, ticketCount);
                break;

            case ReminderSendOutcome.Rejected:
                var next = await reminders.RecordFailureAsync(row.OrderId, result.Error ?? "The reminder was refused.", settings.MaxAttempts, clock.GetUtcNow().AddMinutes(settings.RetryMinutes));
                metrics.Outcome(next == ReminderStatuses.Failed ? "failed" : "retry");
                logger.LogWarning("Reminder for order {OrderId} was refused (attempt {Attempt} of {Max}); now {Status}", row.OrderId, row.Attempts + 1, settings.MaxAttempts, next);
                break;

            default:
                await reminders.MarkNeedsReconciliationAsync(row.OrderId, result.Error ?? "The delivery outcome is unknown.");
                metrics.Outcome("needs_reconciliation");
                logger.LogWarning("Reminder for order {OrderId} has an unknown delivery outcome and will not be retried", row.OrderId);
                break;
        }

        open.Remove(row.OrderId);
    }

    private static string ToSnake(string pascal) =>
        string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
