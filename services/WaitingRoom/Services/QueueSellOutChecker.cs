using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaitingRoom.Service.Clients;
using WaitingRoom.Service.Db;

namespace WaitingRoom.Service.Services;

// The one deliberate, narrow exception to "the waiting room never calls
// Inventory" (ADR-021 amends ADR-002): read-only, on a slow timer, never on
// the join or admission path. A failed check leaves the queue open.
public class QueueSellOutChecker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<QueueSellOutChecker> _logger;
    private readonly TimeSpan _period;

    public QueueSellOutChecker(
        IServiceScopeFactory scopeFactory,
        ILogger<QueueSellOutChecker> logger,
        IOptions<QueueSellOutOptions> options,
        TimeSpan? periodOverride = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _period = periodOverride ?? TimeSpan.FromSeconds(options.Value.CheckIntervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Queue sell-out checker started with period {Period}.", _period);

        using var timer = new PeriodicTimer(_period);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IQueueRepository>();
            var inventoryClient = scope.ServiceProvider.GetRequiredService<IInventoryClient>();

            IReadOnlyList<Guid> activeShowIds;
            try
            {
                activeShowIds = await repository.GetActiveShowIdsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            foreach (var showId in activeShowIds)
            {
                try
                {
                    var soldOut = await inventoryClient.IsSoldOutAsync(showId, stoppingToken);
                    if (soldOut)
                    {
                        await repository.CloseQueueAsync(showId, stoppingToken);
                        _logger.LogInformation("Queue {ShowId} closed: every category sold out.", showId);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (InventoryUnavailableException ex)
                {
                    // Never close a queue because a health check failed.
                    _logger.LogWarning(ex, "Inventory availability check failed for show {ShowId}; leaving the queue open.", showId);
                }
            }
        }

        _logger.LogInformation("Queue sell-out checker stopping.");
    }
}
