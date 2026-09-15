using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Inventory.Service.Db;

namespace Inventory.Service.Services;

public class ExpiredHoldReleaseWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExpiredHoldReleaseWorker> _logger;
    private readonly TimeSpan _period;

    public ExpiredHoldReleaseWorker(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<ExpiredHoldReleaseWorker> logger,
        TimeSpan? period = null)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _period = period ?? TimeSpan.FromSeconds(5); // Check every 5 seconds by default
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Expired hold release worker started with period {Period}.", _period);

        using var timer = new PeriodicTimer(_period);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IHoldRepository>();
                var now = _timeProvider.GetUtcNow();

                int releasedCount = await repository.ReleaseExpiredHoldsAsync(now);
                if (releasedCount > 0)
                {
                    _logger.LogInformation("Expired hold worker automatically released {Count} expired hold(s) at {Timestamp}.", releasedCount, now);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while releasing expired ticket holds.");
            }
        }

        _logger.LogInformation("Expired hold release worker stopping.");
    }
}
