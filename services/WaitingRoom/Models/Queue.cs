namespace WaitingRoom.Service.Models;

public sealed record Queue(
    Guid ShowId,
    DateTimeOffset OnSaleAt,
    DateTimeOffset PrequeueOpensAt,
    long ServingNumber,
    long NextNumber,
    int AdmitBatch,
    int AdmitIntervalSeconds,
    QueueStatus Status);
