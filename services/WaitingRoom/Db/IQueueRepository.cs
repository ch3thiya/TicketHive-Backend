using WaitingRoom.Service.Models;

namespace WaitingRoom.Service.Db;

public interface IQueueRepository
{
    Task<Queue?> GetQueueAsync(Guid showId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the queue row for a high-demand show, or returns the existing
    /// one unchanged if it was already created (an atomic upsert — never a
    /// race between two simultaneous first joiners).
    /// </summary>
    Task<Queue> CreateQueueIfNotExistsAsync(
        Guid showId,
        DateTimeOffset onSaleAt,
        DateTimeOffset prequeueOpensAt,
        int admitBatch,
        int admitIntervalSeconds,
        CancellationToken cancellationToken = default);

    Task<QueueEntry?> GetEntryAsync(Guid showId, string customerSub, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts an entry with a random rank and no queue number. A repeat call for the
    /// same show and customer returns the existing row unchanged (composite primary key upsert).
    /// </summary>
    Task<QueueEntry> JoinPreQueueAsync(Guid showId, string customerSub, DateTimeOffset joinedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts an entry taking the next queue number, incremented atomically. A repeat call for
    /// the same show and customer returns the existing row unchanged.
    /// </summary>
    Task<QueueEntry> JoinPostSaleAsync(Guid showId, string customerSub, DateTimeOffset joinedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Assigns queue_number to every pre-queue entry ordered by random_rank, advances next_number
    /// past the highest assigned, and opens the queue. Guarded by pg_try_advisory_lock so it is
    /// safe to attempt from any instance.
    /// </summary>
    Task RunOnSaleTransitionAsync(Guid showId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances serving_number by admit_batch under pg_try_advisory_lock. Returns the new
    /// serving_number when this call took the lock, or null when another instance held it.
    /// </summary>
    Task<long?> TryAdvanceServingNumberAsync(Guid showId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> GetOpenShowIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>Every queue still in PreQueue whose on_sale_at is now due.</summary>
    Task<IReadOnlyList<Guid>> GetShowIdsDueForOnSaleTransitionAsync(CancellationToken cancellationToken = default);

    /// <summary>Every queue that has not already closed (PreQueue or Open).</summary>
    Task<IReadOnlyList<Guid>> GetActiveShowIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>Idempotent: closing an already-closed queue is a no-op.</summary>
    Task CloseQueueAsync(Guid showId, CancellationToken cancellationToken = default);
}
