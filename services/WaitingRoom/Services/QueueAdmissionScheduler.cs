using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaitingRoom.Service.Db;

namespace WaitingRoom.Service.Services;

// A singleton job: inside TryAdvanceServingNumberAsync the advisory lock
// serializes instances and the last_advanced_at interval check means any
// number of instances can run this loop and a given queue still advances at
// most once per admit interval (concurrency.md).
public class QueueAdmissionScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<QueueAdmissionScheduler> _logger;
    private readonly TimeSpan _period;

    public QueueAdmissionScheduler(
        IServiceScopeFactory scopeFactory,
        ILogger<QueueAdmissionScheduler> logger,
        IOptions<QueueDefaultsOptions> options,
        TimeSpan? periodOverride = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _period = periodOverride ?? TimeSpan.FromSeconds(options.Value.AdmitIntervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Queue admission scheduler started with period {Period}.", _period);

        using var timer = new PeriodicTimer(_period);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IQueueRepository>();

                // Move any queue whose on-sale time has arrived out of
                // PreQueue before advancing anyone, so a lagging transition
                // never lets a post-sale joiner take a number the pre-queue
                // pool hasn't been ranked into yet.
                var dueShowIds = await repository.GetShowIdsDueForOnSaleTransitionAsync(stoppingToken);
                foreach (var showId in dueShowIds)
                {
                    await repository.RunOnSaleTransitionAsync(showId, stoppingToken);
                    _logger.LogInformation("Queue {ShowId} transitioned to Open at on-sale.", showId);
                }

                var openShowIds = await repository.GetOpenShowIdsAsync(stoppingToken);

                foreach (var showId in openShowIds)
                {
                    var newServingNumber = await repository.TryAdvanceServingNumberAsync(showId, stoppingToken);
                    if (newServingNumber is not null)
                    {
                        _logger.LogInformation("Queue {ShowId} serving number advanced to {ServingNumber}.", showId, newServingNumber);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while advancing waiting room queues.");
            }
        }

        _logger.LogInformation("Queue admission scheduler stopping.");
    }
}
