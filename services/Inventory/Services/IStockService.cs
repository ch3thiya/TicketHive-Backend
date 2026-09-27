using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Inventory.Service.Services;

public record InitializeStockCategoryRequest(
    Guid CategoryId,
    int Capacity,
    decimal UnitPrice,
    string Currency,
    string AllocationMode
);

public record InitializeStockRequest(
    Guid OrganizerId,
    DateTimeOffset? OnSaleAt,
    int MaxPerCustomer,
    int HoldMinutes,
    bool HighDemand,
    List<InitializeStockCategoryRequest> Categories,
    int? HighDemandThreshold = null
);

public record StockItemResponse(
    Guid CategoryId,
    int Capacity,
    int Available,
    decimal UnitPrice,
    string Currency
);

public interface IStockService
{
    // Idempotent: calling this again for a show that already has stock never
    // resets available on existing rows.
    Task<List<StockItemResponse>> InitializeAsync(Guid showId, InitializeStockRequest request);

    // Null means the show has never been initialized.
    Task<List<StockItemResponse>?> GetAvailabilityAsync(Guid showId);
}
