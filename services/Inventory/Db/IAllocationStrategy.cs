using System;
using System.Threading.Tasks;
using Npgsql;

namespace Inventory.Service.Db;

// The concurrency control for S2-03 (ADR-007): an implementation must
// decrement stock with a single conditional UPDATE inside the caller's
// transaction and report success purely from rows affected — never by
// reading `available`, comparing in C#, and then updating. Seated
// allocation (Sprint 3) locks individual seat rows behind this same
// interface instead of a GA counter.
public interface IAllocationStrategy
{
    // Null means the WHERE clause matched no row: insufficient stock. A
    // non-null result carries the unit price and currency read atomically
    // from the same UPDATE, for the hold's price snapshot.
    Task<AllocatedStock?> TryAllocateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid showId,
        Guid categoryId,
        int quantity);
}

public record AllocatedStock(decimal UnitPrice, string Currency);
