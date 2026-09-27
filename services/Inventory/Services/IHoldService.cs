using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Inventory.Service.Models;

namespace Inventory.Service.Services;

public record CreateHoldItemRequest(Guid CategoryId, int Quantity);

public record CreateHoldRequest(Guid ShowId, List<CreateHoldItemRequest> Items);

public record HoldItemResponse(Guid CategoryId, int Quantity, decimal UnitPrice, string Currency);

public record HoldResponse(Guid HoldId, Guid ShowId, string Status, DateTimeOffset ExpiresAt, List<HoldItemResponse> Items);

// Booking's own request/response shape (ADR-020 tolerant reader) — includes
// CustomerSub, which the customer-facing HoldResponse has no reason to.
public record InternalHoldResponse(Guid HoldId, Guid ShowId, string CustomerSub, string Status, DateTimeOffset ExpiresAt, List<HoldItemResponse> Items);

public enum CreateHoldStatus
{
    Created,
    Duplicate,
    ShowNotFound,
    CategoryNotFound,
    StockUnavailable,
    QuotaExceeded,
    HighDemandBlocked
}

public class CreateHoldResult
{
    public required CreateHoldStatus Status { get; init; }
    public HoldResponse? Hold { get; init; }
    public Guid? CategoryId { get; init; }
    public int? Limit { get; init; }
}

public interface IHoldService
{
    // Throws ArgumentException for 400s (empty items, non-positive
    // quantity) — the Idempotency-Key header itself is an HTTP concern the
    // controller checks before calling this. Every other outcome (quota,
    // stock, the idempotent replay, the high-demand gate) comes back as
    // CreateHoldResult so the controller maps status codes without any
    // business logic of its own.
    Task<CreateHoldResult> CreateHoldAsync(string customerSub, string idempotencyKey, bool hasAdmissionToken, CreateHoldRequest request, string? admissionToken = null);

    // Null if the hold does not exist. Ownership (comparing the caller's
    // sub against CustomerSub) is left to the controller, which is the one
    // that decides between 404 and leaking existence with a 403.
    Task<Hold?> GetHoldAsync(Guid holdId);

    Task<bool> CancelHoldAsync(Guid holdId, string customerSub);

    Task<Hold?> GetActiveHoldForCustomerAsync(Guid showId, string customerSub);

    Task<bool> FreezeHoldAsync(Guid holdId);
    Task<bool> ConvertHoldAsync(Guid holdId);
    Task<bool> ReleaseHoldAsync(Guid holdId);

    HoldResponse ToResponse(Hold hold);
    InternalHoldResponse ToInternalResponse(Hold hold);
}
