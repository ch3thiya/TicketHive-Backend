using WaitingRoom.Service.Db;
using WaitingRoom.Service.Models;

namespace WaitingRoom.Service.Services;

public class QueueService : IQueueService
{
    private readonly IQueueRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly IAdmissionTokenIssuer _admissionTokenIssuer;

    public QueueService(IQueueRepository repository, TimeProvider timeProvider, IAdmissionTokenIssuer admissionTokenIssuer)
    {
        _repository = repository;
        _timeProvider = timeProvider;
        _admissionTokenIssuer = admissionTokenIssuer;
    }

    public async Task<QueueEntry?> JoinAsync(Guid showId, string customerSub, CancellationToken cancellationToken = default)
    {
        var queue = await _repository.GetQueueAsync(showId, cancellationToken);
        var now = _timeProvider.GetUtcNow();

        if (queue is null || now < queue.PrequeueOpensAt)
        {
            return null;
        }

        return now < queue.OnSaleAt
            ? await _repository.JoinPreQueueAsync(showId, customerSub, now, cancellationToken)
            : await _repository.JoinPostSaleAsync(showId, customerSub, now, cancellationToken);
    }

    public QueueEntryResponse ToResponse(QueueEntry entry) =>
        new(entry.ShowId, entry.QueueNumber, entry.JoinedAt);

    public async Task<QueuePositionResponse> GetPositionAsync(Guid showId, string customerSub, CancellationToken cancellationToken = default)
    {
        var entry = await _repository.GetEntryAsync(showId, customerSub, cancellationToken);
        if (entry is null)
        {
            return new QueuePositionResponse(QueuePositionStatus.NotInQueue, null, null, null, null);
        }

        if (entry.AdmittedAt is not null)
        {
            var (token, expiresAt) = _admissionTokenIssuer.Issue(showId, customerSub, entry.AdmittedAt.Value);
            return new QueuePositionResponse(QueuePositionStatus.Admitted, null, null, token, expiresAt);
        }

        var queue = await _repository.GetQueueAsync(showId, cancellationToken);

        if (entry.QueueNumber is null)
        {
            // Still in the pre-queue: no position yet, only the sale time.
            return new QueuePositionResponse(QueuePositionStatus.Waiting, null, queue?.OnSaleAt, null, null);
        }

        var position = entry.QueueNumber.Value - (queue?.ServingNumber ?? 0);
        return new QueuePositionResponse(QueuePositionStatus.Waiting, position, null, null, null);
    }
}
