namespace WaitingRoom.Service.Models;

public sealed record QueueEntry(
    Guid ShowId,
    string CustomerSub,
    double RandomRank,
    long? QueueNumber,
    DateTimeOffset JoinedAt,
    DateTimeOffset? AdmittedAt);
