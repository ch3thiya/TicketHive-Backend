using Microsoft.Extensions.Options;
using WaitingRoom.Service.Clients;
using WaitingRoom.Service.Db;
using WaitingRoom.Service.Models;

namespace WaitingRoom.Service.Services;

public class QueueService : IQueueService
{
    private readonly IQueueRepository _repository;
    private readonly ICatalogClient _catalogClient;
    private readonly TimeProvider _timeProvider;
    private readonly IAdmissionTokenIssuer _admissionTokenIssuer;
    private readonly QueueDefaultsOptions _queueDefaults;

    public QueueService(
        IQueueRepository repository,
        ICatalogClient catalogClient,
        TimeProvider timeProvider,
        IAdmissionTokenIssuer admissionTokenIssuer,
        IOptions<QueueDefaultsOptions> queueDefaults)
    {
        _repository = repository;
        _catalogClient = catalogClient;
        _timeProvider = timeProvider;
        _admissionTokenIssuer = admissionTokenIssuer;
        _queueDefaults = queueDefaults.Value;
    }

    public async Task<QueueEntry?> JoinAsync(Guid showId, string customerSub, CancellationToken cancellationToken = default)
    {
        var queue = await _repository.GetQueueAsync(showId, cancellationToken)
            ?? await ProvisionQueueIfHighDemandAsync(showId, cancellationToken);
        var now = _timeProvider.GetUtcNow();

        if (queue is null || now < queue.PrequeueOpensAt)
        {
            return null;
        }

        // The scheduler transitions a queue at on-sale, but a join landing
        // in the gap before its next tick must never be routed on OnSaleAt
        // alone — that is how a post-sale joiner ended up with queue_number
        // 0 while PreQueue entries sat with no number at all. Route on the
        // queue's actual status, running the transition inline if it is overdue.
        if (queue.Status == QueueStatus.PreQueue && now >= queue.OnSaleAt)
        {
            await _repository.RunOnSaleTransitionAsync(showId, cancellationToken);
            queue = await _repository.GetQueueAsync(showId, cancellationToken) ?? queue;
        }

        return queue.Status == QueueStatus.PreQueue
            ? await _repository.JoinPreQueueAsync(showId, customerSub, now, cancellationToken)
            : await _repository.JoinPostSaleAsync(showId, customerSub, now, cancellationToken);
    }

    // A show not yet seen by this queue: ask Catalog whether it is
    // high-demand and, if so, seed a queue row from our own configured
    // defaults (CatalogUnavailableException propagates to the caller —
    // an outage never silently reports "no queue").
    private async Task<Queue?> ProvisionQueueIfHighDemandAsync(Guid showId, CancellationToken cancellationToken)
    {
        var salesRules = await _catalogClient.GetSalesRulesAsync(showId, cancellationToken);
        if (salesRules is null || !salesRules.HighDemand || salesRules.OnSaleAt is null)
        {
            return null;
        }

        var prequeueOpensAt = salesRules.OnSaleAt.Value.AddMinutes(-_queueDefaults.PrequeueWindowMinutes);
        return await _repository.CreateQueueIfNotExistsAsync(
            showId,
            salesRules.OnSaleAt.Value,
            prequeueOpensAt,
            _queueDefaults.AdmitBatch,
            _queueDefaults.AdmitIntervalSeconds,
            cancellationToken);
    }

    public QueueEntryResponse ToResponse(QueueEntry entry) =>
        new(entry.ShowId, entry.QueueNumber, entry.JoinedAt);

    public async Task<QueuePositionResponse> GetPositionAsync(Guid showId, string customerSub, CancellationToken cancellationToken = default)
    {
        var entry = await _repository.GetEntryAsync(showId, customerSub, cancellationToken);
        if (entry is null)
        {
            return new QueuePositionResponse(QueuePositionStatus.NotInQueue.ToString(), null, null, null, null);
        }

        if (entry.AdmittedAt is not null)
        {
            var (token, expiresAt) = _admissionTokenIssuer.Issue(showId, customerSub, entry.AdmittedAt.Value);
            return new QueuePositionResponse(QueuePositionStatus.Admitted.ToString(), null, null, token, expiresAt);
        }

        var queue = await _repository.GetQueueAsync(showId, cancellationToken);

        if (queue?.Status == QueueStatus.Closed)
        {
            return new QueuePositionResponse(QueuePositionStatus.SoldOut.ToString(), null, null, null, null);
        }

        if (entry.QueueNumber is null)
        {
            // Still in the pre-queue: no position yet, only the sale time.
            return new QueuePositionResponse(QueuePositionStatus.Waiting.ToString(), null, queue?.OnSaleAt, null, null);
        }

        var position = entry.QueueNumber.Value - (queue?.ServingNumber ?? 0);
        return new QueuePositionResponse(QueuePositionStatus.Waiting.ToString(), position, null, null, null);
    }
}
