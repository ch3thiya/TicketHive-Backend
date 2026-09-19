using WaitingRoom.Service.Models;

namespace WaitingRoom.Service.Services;

public record QueueEntryResponse(Guid ShowId, long? QueueNumber, DateTimeOffset JoinedAt);

public interface IQueueService
{
    /// <summary>
    /// Joins the show's queue, or returns null when there is no queue yet
    /// (a normal show, or a high-demand show before its pre-queue window).
    /// Calling again for the same show and customer returns the existing entry.
    /// </summary>
    Task<QueueEntry?> JoinAsync(Guid showId, string customerSub, CancellationToken cancellationToken = default);

    QueueEntryResponse ToResponse(QueueEntry entry);
}
