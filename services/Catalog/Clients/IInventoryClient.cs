using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Catalog.Service.Clients;

public record InitializeShowStockCategory(
    Guid CategoryId,
    int Capacity,
    decimal UnitPrice,
    string Currency,
    string AllocationMode
);

public record InitializeShowStockRequest(
    Guid OrganizerId,
    DateTimeOffset? OnSaleAt,
    int MaxPerCustomer,
    int HoldMinutes,
    bool HighDemand,
    List<InitializeShowStockCategory> Categories,
    int? HighDemandThreshold = null
);

public interface IInventoryClient
{
    /// <summary>
    /// Idempotent: initializes a show's stock in Inventory. Throws
    /// <see cref="InventoryUnavailableException"/> for any failure — a
    /// timeout, a non-success status, or a failure to acquire the
    /// machine-to-machine token — never a generic HttpRequestException.
    /// </summary>
    Task InitializeShowStockAsync(Guid showId, InitializeShowStockRequest request, CancellationToken cancellationToken = default);
}
