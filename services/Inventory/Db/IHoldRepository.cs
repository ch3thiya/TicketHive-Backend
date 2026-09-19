using System;
using System.Threading.Tasks;
using Inventory.Service.Models;

namespace Inventory.Service.Db;

public enum HoldCreationOutcome
{
    Created,
    Duplicate,
    QuotaExceeded,
    CategoryNotFound,
    StockUnavailable
}

public class HoldCreationResult
{
    public required HoldCreationOutcome Outcome { get; init; }
    public Hold? Hold { get; init; }
    public Guid? CategoryId { get; init; }
    public int? Limit { get; init; }
}

public interface IHoldRepository
{
    // Null means the show has never been initialized.
    Task<ShowRules?> GetShowRulesAsync(Guid showId);

    // Runs the whole hold transaction (ADR-007): category existence, the
    // quota update, the per-category stock allocation in ascending
    // category_id order, and the hold + hold_items insert. Any refusal
    // rolls back the whole transaction. A retry with the same
    // (customer_sub, idempotency_key) surfaces as HoldCreationOutcome.Duplicate
    // with the original hold attached — never a second row.
    Task<HoldCreationResult> CreateAsync(Hold hold, int maxPerCustomer);

    // Returns the hold with its items (currency joined from stock), or null
    // if it does not exist.
    Task<Hold?> GetByIdAsync(Guid holdId);

    // Scans for up to batchSize active holds whose expires_at timestamp has
    // passed, oldest expiry first, releases held ticket quantities back to
    // stock, decrements customer quotas, and updates status to 'Expired'.
    // Returns count of released holds.
    Task<int> ReleaseExpiredHoldsAsync(DateTimeOffset now, int batchSize);
}
