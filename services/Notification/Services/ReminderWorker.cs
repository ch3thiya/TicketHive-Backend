using BuildingBlocks;
using Microsoft.Extensions.Options;

namespace Notification.Service.Services;

/// <summary>
/// Runs the reminder job on a timer. Any number of instances may run it: the database hands each
/// reminder to one of them. It stays idle until the service-to-service credentials it needs are set.
/// </summary>
public class ReminderWorker(
    IServiceScopeFactory scopes,
    IOptions<ReminderOptions> options,
    IOptions<InternalServiceTokenClientOptions> tokenOptions,
    ILogger<ReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reportedIdle = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = options.Value;
            if (settings.Enabled && DependenciesConfigured(tokenOptions.Value))
            {
                reportedIdle = false;
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<ReminderProcessor>().RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Reminder pass failed; it will run again");
                }
            }
            else if (!reportedIdle)
            {
                reportedIdle = true;
                logger.LogInformation("Reminder job is idle: {Reason}", settings.Enabled ? "service credentials (Wso2:InternalApi) are not configured" : "Reminders:Enabled is false");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, settings.PollSeconds)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public static bool DependenciesConfigured(InternalServiceTokenClientOptions token) =>
        !string.IsNullOrWhiteSpace(token.ClientId)
        && !token.ClientId.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(token.ClientSecret)
        && !token.ClientSecret.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase);
}
