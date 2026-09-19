using WaitingRoom.Service.Models;

namespace WaitingRoom.Service.Db;

public class QueueRepository : IQueueRepository
{
    private readonly DbConnectionFactory _connectionFactory;

    public QueueRepository(DbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public Task<Queue?> GetQueueAsync(Guid showId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Added when the queue and queue entry tables land.");

    public Task<QueueEntry?> GetEntryAsync(Guid showId, string customerSub, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Added when the queue and queue entry tables land.");

    public Task<QueueEntry> JoinPreQueueAsync(Guid showId, string customerSub, DateTimeOffset joinedAt, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Added when joining a queue is implemented.");

    public Task<QueueEntry> JoinPostSaleAsync(Guid showId, string customerSub, DateTimeOffset joinedAt, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Added when joining a queue is implemented.");

    public Task RunOnSaleTransitionAsync(Guid showId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Added when the on-sale transition is implemented.");

    public Task<long?> TryAdvanceServingNumberAsync(Guid showId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Added when the scheduler is implemented.");
}
