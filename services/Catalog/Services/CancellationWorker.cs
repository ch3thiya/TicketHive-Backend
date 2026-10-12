using Catalog.Service.Clients;
using Catalog.Service.Db;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Catalog.Service.Services;

public class CancellationWorker(IServiceScopeFactory scopes, ILogger<CancellationWorker> logger, Meter meter) : BackgroundService
{
    private readonly Counter<long> _dispatched = meter.CreateCounter<long>("cancellation.shows.dispatched");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<CancellationRepository>();
                var client = scope.ServiceProvider.GetRequiredService<CancellationClient>();
                for (var i = 0; i < 100 && !stoppingToken.IsCancellationRequested; i++)
                {
                    var work = await repository.ClaimAsync();
                    if (work is null) break;
                    using var activity = new Activity("ShowCancellation.Dispatch").Start();
                    activity.SetTag("show.id", work.Value.ShowId);
                    var stopped = false;
                    try
                    {
                        await client.EstablishCancellationBarrierAsync(work.Value.ShowId);
                        stopped = true;
                        await repository.FinishAsync(work.Value.ShowId, work.Value.Token, true, true);
                        _dispatched.Add(1);
                        logger.LogInformation("Dispatched cancellation for show {ShowId}", work.Value.ShowId);
                    }
                    catch (Exception ex)
                    {
                        await repository.FinishAsync(work.Value.ShowId, work.Value.Token, stopped, false);
                        logger.LogWarning(ex, "Show cancellation {ShowId} will retry", work.Value.ShowId);
                    }
                }
            }
            catch (Exception ex) { logger.LogWarning(ex, "Show cancellation recovery pass will retry"); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
