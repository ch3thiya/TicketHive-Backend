using WaitingRoom.Service.Db;
using WaitingRoom.Service.Models;

namespace WaitingRoom.Service.Services;

public class QueueService : IQueueService
{
    private readonly IQueueRepository _repository;
    private readonly TimeProvider _timeProvider;

    public QueueService(IQueueRepository repository, TimeProvider timeProvider)
    {
        _repository = repository;
        _timeProvider = timeProvider;
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
}
