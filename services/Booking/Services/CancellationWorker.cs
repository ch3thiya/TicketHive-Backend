using Booking.Service.Clients;
using Booking.Service.Db;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Booking.Service.Services;

public class CancellationWorker(IServiceScopeFactory scopes, ILogger<CancellationWorker> logger, Meter meter) : BackgroundService
{
    private readonly Counter<long> _completed = meter.CreateCounter<long>("cancellation.effects.completed");
    private readonly Counter<long> _retries = meter.CreateCounter<long>("cancellation.effects.retries");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Cancellation recovery pass will retry"); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    public async Task ProcessBatchAsync(CancellationToken stoppingToken = default)
    {
        using var scope = scopes.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<CancellationRepository>();
        var orders = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var client = scope.ServiceProvider.GetRequiredService<CancellationClient>();
        for (var i = 0; i < 100 && !stoppingToken.IsCancellationRequested; i++)
        {
            var work = await repository.ClaimAsync();
            if (work is null) break;
            using var activity = new Activity("Cancellation.Process").Start();
            activity.SetTag("order.id", work.Value.Id);
            try
            {
                var order = await orders.GetByIdAsync(work.Value.Id) ?? throw new InvalidOperationException("Order missing.");
                var state = (await repository.GetAsync(order.Id))!;
                await client.ReturnAsync(order.HoldId);
                var refund = state.RefundStatus == "Pending" ? await client.RefundAsync(order.Id, order.TotalAmount, order.Currency) : state.RefundStatus;
                await repository.FinishAsync(order.Id, work.Value.Token, refund, null);
                _completed.Add(1);
                logger.LogInformation("Cancellation effects completed for {OrderId} with refund {RefundStatus}", order.Id, refund);
            }
            catch (Exception ex)
            {
                _retries.Add(1);
                await repository.FinishAsync(work.Value.Id, work.Value.Token, null, ex.GetType().Name);
                logger.LogWarning(ex, "Cancellation {OrderId} will retry", work.Value.Id);
            }
        }
        foreach (var email in await repository.ReadyEmailsAsync())
            await repository.RecordEmailAsync(email.OrderIds, await client.NotifyAsync(email));
    }
}
