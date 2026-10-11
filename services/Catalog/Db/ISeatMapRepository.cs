using Catalog.Service.Models;

namespace Catalog.Service.Db;

public enum CreateSeatMapRequestOutcome
{
    Created,
    ShowNotFound,
    AlreadySeated,
    PendingExists
}

public enum CreateDraftOutcome
{
    Created,
    RequestNotFound,
    RequestNotPending
}

public enum CompleteEnableOutcome
{
    Enabled,
    AlreadyEnabled,
    LayoutNotFound,
    NotEnableable
}

public enum RejectOutcome
{
    Rejected,
    NotFound,
    NotPending
}

public interface ISeatMapRepository
{
    Task<CreateSeatMapRequestOutcome> CreateRequestAsync(SeatMapRequest request);

    Task<SeatMapRequest?> GetRequestAsync(Guid requestId);

    Task<List<SeatMapRequest>> GetRequestsForShowAsync(Guid showId);

    Task<List<SeatMapRequest>> ListRequestsAsync(string? status, int limit);

    Task<List<SeatLayout>> GetLayoutsForShowAsync(Guid showId);

    Task<SeatLayout?> GetLayoutAsync(Guid layoutId);

    // Numbers the layout under a lock on the show row, so concurrent drafts get distinct versions.
    Task<(CreateDraftOutcome Outcome, SeatLayout? Layout)> CreateDraftAsync(
        Guid requestId, SeatLayoutSpec spec, string createdBy, DateTimeOffset now);

    // Marks the layout Enabled, the request Completed and the show Seated in one transaction,
    // superseding any earlier enabled layout. Repeating it for an enabled layout changes nothing.
    Task<CompleteEnableOutcome> CompleteEnableAsync(Guid layoutId, string decidedBy, DateTimeOffset now);

    Task<RejectOutcome> RejectAsync(Guid requestId, string reason, string decidedBy, DateTimeOffset now);
}
