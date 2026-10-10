using System.Diagnostics.Metrics;
using Notification.Service.Db;

namespace Notification.Service.Services;

public class CancellationEmailWorker(IServiceScopeFactory scopes, ILogger<CancellationEmailWorker> logger, Meter meter) : BackgroundService
{
    private readonly Counter<long> _outcomes = meter.CreateCounter<long>("cancellation.email.outcomes");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<CancellationEmailRepository>();
                var sender = scope.ServiceProvider.GetRequiredService<CancellationEmailSender>();
                for (var i = 0; i < 100 && !stoppingToken.IsCancellationRequested; i++)
                {
                    var email = await repository.ClaimAsync();
                    if (email is null) break;
                    string status;
                    try { status = await sender.SendAsync(email); }
                    catch (Exception ex)
                    {
                        status = "NeedsReconciliation";
                        logger.LogWarning(ex, "Cancellation email delivery is ambiguous for show {ShowId}", email.ShowId);
                    }
                    await repository.CompleteAsync(email.Key, status);
                    _outcomes.Add(1, new KeyValuePair<string, object?>("outcome", status));
                    logger.LogInformation("Cancellation email for show {ShowId} has outcome {Outcome}", email.ShowId, status);
                }
            }
            catch (Exception ex) { logger.LogWarning(ex, "Cancellation email recovery will retry"); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
