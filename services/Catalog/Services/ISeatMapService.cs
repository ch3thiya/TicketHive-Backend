using Catalog.Service.Models;

namespace Catalog.Service.Services;

public enum SeatMapStatus
{
    Ok,
    NotFound,
    Forbidden,
    Conflict,
    Invalid,
    Unavailable
}

public record SeatMapResult<T>(SeatMapStatus Status, T? Value = default, string? Detail = null, IReadOnlyList<LayoutError>? Errors = null, string? Code = null)
{
    public static SeatMapResult<T> Ok(T value) => new(SeatMapStatus.Ok, value);
    public static SeatMapResult<T> Fail(SeatMapStatus status, string detail, string? code = null) => new(status, default, detail, null, code);
}

public record SeatMapDetail(
    SeatMapRequest Request,
    string EventName,
    DateOnly ShowDate,
    TimeOnly ShowTime,
    string SeatingMode,
    Guid? VenueId,
    IReadOnlyList<TicketCategory> Categories,
    IReadOnlyList<SeatLayout> Layouts);

public interface ISeatMapService
{
    Task<SeatMapResult<SeatMapRequest>> RequestAsync(Guid organizerId, Guid showId, string? description);

    Task<SeatMapResult<IReadOnlyList<SeatMapRequest>>> GetForOrganizerAsync(Guid organizerId, Guid showId);

    Task<SeatMapResult<IReadOnlyList<SeatMapRequestSummary>>> ListAsync(string? status);

    Task<SeatMapResult<SeatMapDetail>> GetDetailAsync(Guid requestId);

    Task<SeatMapResult<SeatLayout>> CreateDraftAsync(Guid requestId, SeatLayoutSpec spec, string adminSub);

    Task<SeatMapResult<SeatLayout>> EnableAsync(Guid layoutId, string adminSub);

    Task<SeatMapResult<bool>> DeleteLayoutAsync(Guid layoutId);

    Task<SeatMapResult<SeatMapRequest>> RejectAsync(Guid requestId, string? reason, string adminSub);
}
