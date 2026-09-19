using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Inventory.Service.Db;
using Inventory.Service.Models;

namespace Inventory.Service.Services;

public class ExpiredHoldReleaseWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExpiredHoldReleaseWorker> _logger;
    private readonly TimeSpan _period;
    private readonly int _batchSize;

    public ExpiredHoldReleaseWorker(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<ExpiredHoldReleaseWorker> logger,
        IOptions<HoldExpirySweepOptions> options,
        TimeSpan? periodOverride = null)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _period = periodOverride ?? TimeSpan.FromSeconds(options.Value.IntervalSeconds);
        _batchSize = options.Value.BatchSize;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Expired hold release worker started with period {Period} and batch size {BatchSize}.", _period, _batchSize);

        using var timer = new PeriodicTimer(_period);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IHoldRepository>();
                var now = _timeProvider.GetUtcNow();

                int releasedCount = await repository.ReleaseExpiredHoldsAsync(now, _batchSize);
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
