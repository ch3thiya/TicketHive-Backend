using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using Inventory.Service.Models;

namespace Inventory.Service.Db;

public class HoldRepository : IHoldRepository
{
    private readonly DbConnectionFactory _connectionFactory;
    private readonly IAllocationStrategy _allocationStrategy;

    public HoldRepository(DbConnectionFactory connectionFactory, IAllocationStrategy allocationStrategy)
    {
        _connectionFactory = connectionFactory;
        _allocationStrategy = allocationStrategy;
    }

    public async Task<ShowRules?> GetShowRulesAsync(Guid showId)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT show_id, organizer_id, on_sale_at, max_per_customer, hold_minutes, high_demand, high_demand_threshold
            FROM show_rules
            WHERE show_id = @ShowId;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ShowId", showId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new ShowRules
        {
            ShowId = reader.GetGuid(0),
            OrganizerId = reader.GetGuid(1),
            OnSaleAt = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            MaxPerCustomer = reader.GetInt32(3),
            HoldMinutes = reader.GetInt32(4),
            HighDemand = reader.GetBoolean(5),
            HighDemandThreshold = reader.IsDBNull(6) ? null : reader.GetInt32(6)
        };
    }

    public async Task<int> GetTotalHeldOrSoldAsync(Guid showId)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT COALESCE(SUM(capacity - available), 0)
            FROM stock
            WHERE show_id = @ShowId;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ShowId", showId);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task<int> GetTotalActiveHoldsAsync(Guid showId, DateTimeOffset now)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT COUNT(id)
            FROM holds
            WHERE show_id = @ShowId AND status = 'Active' AND expires_at > @Now;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ShowId", showId);
        command.Parameters.AddWithValue("Now", now);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task<HoldCreationResult> CreateAsync(Hold hold, int maxPerCustomer)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // Existence only, never availability — categories are never deleted
        // once a show is initialized (ADR-004), so this carries none of the
        // race the stock allocation below guards against.
        var requestedCategoryIds = hold.Items.Select(i => i.CategoryId).Distinct().ToList();
        var knownCategoryIds = await GetKnownCategoryIdsAsync(connection, transaction, hold.ShowId, requestedCategoryIds);
        var missingCategoryIds = requestedCategoryIds.Where(id => !knownCategoryIds.Contains(id)).ToList();
        if (missingCategoryIds.Count > 0)
        {
            await transaction.RollbackAsync();
            return new HoldCreationResult { Outcome = HoldCreationOutcome.CategoryNotFound, CategoryId = missingCategoryIds[0] };
        }

        var totalQuantity = hold.Items.Sum(i => i.Quantity);

        const string quotaInsertSql = @"
            INSERT INTO customer_quotas (show_id, customer_sub, quantity)
            VALUES (@ShowId, @CustomerSub, 0)
            ON CONFLICT DO NOTHING;
        ";
        await using (var command = new NpgsqlCommand(quotaInsertSql, connection, transaction))
        {
            command.Parameters.AddWithValue("ShowId", hold.ShowId);
            command.Parameters.AddWithValue("CustomerSub", hold.CustomerSub);
            await command.ExecuteNonQueryAsync();
        }

        const string quotaUpdateSql = @"
            UPDATE customer_quotas SET quantity = quantity + @Total
             WHERE show_id = @ShowId AND customer_sub = @CustomerSub
               AND quantity + @Total <= @Max;
        ";
        int quotaRowsAffected;
        await using (var command = new NpgsqlCommand(quotaUpdateSql, connection, transaction))
        {
            command.Parameters.AddWithValue("ShowId", hold.ShowId);
            command.Parameters.AddWithValue("CustomerSub", hold.CustomerSub);
            command.Parameters.AddWithValue("Total", totalQuantity);
            command.Parameters.AddWithValue("Max", maxPerCustomer);
            quotaRowsAffected = await command.ExecuteNonQueryAsync();
        }

        // Zero rows affected means the limit would be exceeded — an
        // ordinary outcome (422), not a logged error (ADR-009).
        if (quotaRowsAffected == 0)
        {
            await transaction.RollbackAsync();
            return new HoldCreationResult { Outcome = HoldCreationOutcome.QuotaExceeded, Limit = maxPerCustomer };
        }

        // Fixed category_id order avoids deadlocking against a concurrent
        // hold for the same show touching the same categories in a
        // different order (ADR-007).
        foreach (var item in hold.Items.OrderBy(i => i.CategoryId))
        {
            var allocated = await _allocationStrategy.TryAllocateAsync(connection, transaction, hold.ShowId, item.CategoryId, item.Quantity);
            if (allocated is null)
            {
                await transaction.RollbackAsync();
                return new HoldCreationResult { Outcome = HoldCreationOutcome.StockUnavailable, CategoryId = item.CategoryId };
            }

            item.UnitPrice = allocated.UnitPrice;
            item.Currency = allocated.Currency;
        }

        const string holdInsertSql = @"
            INSERT INTO holds (id, show_id, customer_sub, status, expires_at, idempotency_key, created_at)
            VALUES (@Id, @ShowId, @CustomerSub, @Status, @ExpiresAt, @IdempotencyKey, @CreatedAt);
        ";

        const string holdItemInsertSql = @"
            INSERT INTO hold_items (hold_id, category_id, quantity, unit_price)
            VALUES (@HoldId, @CategoryId, @Quantity, @UnitPrice);
        ";

        try
        {
            await using (var command = new NpgsqlCommand(holdInsertSql, connection, transaction))
            {
                command.Parameters.AddWithValue("Id", hold.Id);
                command.Parameters.AddWithValue("ShowId", hold.ShowId);
                command.Parameters.AddWithValue("CustomerSub", hold.CustomerSub);
                command.Parameters.AddWithValue("Status", hold.Status.ToString());
                command.Parameters.AddWithValue("ExpiresAt", hold.ExpiresAt);
                command.Parameters.AddWithValue("IdempotencyKey", hold.IdempotencyKey);
                command.Parameters.AddWithValue("CreatedAt", hold.CreatedAt);
                await command.ExecuteNonQueryAsync();
            }

            foreach (var item in hold.Items)
            {
                await using var command = new NpgsqlCommand(holdItemInsertSql, connection, transaction);
                command.Parameters.AddWithValue("HoldId", hold.Id);
                command.Parameters.AddWithValue("CategoryId", item.CategoryId);
                command.Parameters.AddWithValue("Quantity", item.Quantity);
                command.Parameters.AddWithValue("UnitPrice", item.UnitPrice);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            return new HoldCreationResult { Outcome = HoldCreationOutcome.Created, Hold = hold };
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // (customer_sub, idempotency_key) already has a row: this is a
            // retry, not a new hold. Roll back everything this attempt did
            // (quota, stock) and return the original.
            await transaction.RollbackAsync();
            var existing = await FindByIdempotencyKeyAsync(hold.CustomerSub, hold.IdempotencyKey);
            return new HoldCreationResult { Outcome = HoldCreationOutcome.Duplicate, Hold = existing };
        }
    }

    public async Task<Hold?> GetByIdAsync(Guid holdId)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        return await GetByIdAsync(holdId, connection, null);
    }

    private async Task<Hold?> FindByIdempotencyKeyAsync(string customerSub, string idempotencyKey)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = "SELECT id FROM holds WHERE customer_sub = @CustomerSub AND idempotency_key = @IdempotencyKey;";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("CustomerSub", customerSub);
        command.Parameters.AddWithValue("IdempotencyKey", idempotencyKey);

        var holdId = await command.ExecuteScalarAsync();
        return holdId is null ? null : await GetByIdAsync((Guid)holdId, connection, null);
    }

    private static async Task<Hold?> GetByIdAsync(Guid holdId, NpgsqlConnection connection, NpgsqlTransaction? transaction)
    {
        const string sql = @"
            SELECT h.id, h.show_id, h.customer_sub, h.status, h.expires_at, h.idempotency_key, h.created_at,
                   hi.category_id, hi.quantity, hi.unit_price, s.currency
            FROM holds h
            JOIN hold_items hi ON hi.hold_id = h.id
            JOIN stock s ON s.show_id = h.show_id AND s.category_id = hi.category_id
            WHERE h.id = @HoldId
            ORDER BY hi.category_id;
        ";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("HoldId", holdId);

        await using var reader = await command.ExecuteReaderAsync();
        Hold? hold = null;
        while (await reader.ReadAsync())
        {
            hold ??= new Hold
            {
                Id = reader.GetGuid(0),
                ShowId = reader.GetGuid(1),
                CustomerSub = reader.GetString(2),
                Status = Enum.Parse<HoldStatus>(reader.GetString(3)),
                ExpiresAt = reader.GetFieldValue<DateTimeOffset>(4),
                IdempotencyKey = reader.GetString(5),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(6)
            };

            hold.Items.Add(new HoldItem
            {
                HoldId = hold.Id,
                CategoryId = reader.GetGuid(7),
                Quantity = reader.GetInt32(8),
                UnitPrice = reader.GetDecimal(9),
                Currency = reader.GetString(10).Trim()
            });
        }

        return hold;
    }

    public async Task<HoldReleaseSummary> ReleaseExpiredHoldsAsync(DateTimeOffset now, int batchSize = 100)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // 1. Fetch expired active holds with row locks, oldest expiry
        // first and capped at batchSize so one pass never locks the whole
        // table during an on-sale with thousands of simultaneous expiries.
        const string selectExpiredSql = @"
            SELECT id, show_id, customer_sub
            FROM holds
            WHERE status = 'Active' AND expires_at <= @Now
            ORDER BY expires_at
            LIMIT @BatchSize
            FOR UPDATE SKIP LOCKED;
        ";

        var expiredHolds = new List<(Guid HoldId, Guid ShowId, string CustomerSub)>();
        await using (var command = new NpgsqlCommand(selectExpiredSql, connection, transaction))
        {
            command.Parameters.AddWithValue("Now", now);
            command.Parameters.AddWithValue("BatchSize", batchSize);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                expiredHolds.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)));
            }
        }

        if (expiredHolds.Count == 0)
        {
            await transaction.CommitAsync();
            return new HoldReleaseSummary { HoldsReleased = 0, TicketsReturned = 0, QuotaClampCount = 0 };
        }

        // 2. Process each expired hold: update status, restore stock and quotas
        int totalTicketsReturned = 0;
        int quotaClampCount = 0;

        foreach (var (holdId, showId, customerSub) in expiredHolds)
        {
            const string updateHoldSql = "UPDATE holds SET status = 'Expired' WHERE id = @HoldId;";
            await using (var command = new NpgsqlCommand(updateHoldSql, connection, transaction))
            {
                command.Parameters.AddWithValue("HoldId", holdId);
                await command.ExecuteNonQueryAsync();
            }

            const string selectItemsSql = "SELECT category_id, quantity FROM hold_items WHERE hold_id = @HoldId;";
            var items = new List<(Guid CategoryId, int Quantity)>();
            await using (var command = new NpgsqlCommand(selectItemsSql, connection, transaction))
            {
                command.Parameters.AddWithValue("HoldId", holdId);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    items.Add((reader.GetGuid(0), reader.GetInt32(1)));
                }
            }

            int totalQuantity = items.Sum(i => i.Quantity);
            totalTicketsReturned += totalQuantity;

            foreach (var (categoryId, quantity) in items)
            {
                // Scoped by (show_id, category_id) — the table's actual
                // primary key — so this can never touch another show's
                // stock if a category id were ever reused.
                const string restoreStockSql = @"
                    UPDATE stock
                    SET available = available + @Quantity
                    WHERE show_id = @ShowId AND category_id = @CategoryId;
                ";
                await using var command = new NpgsqlCommand(restoreStockSql, connection, transaction);
                command.Parameters.AddWithValue("Quantity", quantity);
                command.Parameters.AddWithValue("ShowId", showId);
                command.Parameters.AddWithValue("CategoryId", categoryId);
                await command.ExecuteNonQueryAsync();
            }

            // The "before" CTE takes FOR UPDATE so a concurrent release for
            // the same (show_id, customer_sub) blocks and re-reads the
            // committed value instead of racing this one. Comparing it
            // against the post-clamp quantity says whether the clamp
            // engaged, without a second round trip.
            const string restoreQuotaSql = @"
                WITH before AS (
                    SELECT quantity FROM customer_quotas
                    WHERE show_id = @ShowId AND customer_sub = @CustomerSub
                    FOR UPDATE
                )
                UPDATE customer_quotas
                SET quantity = GREATEST(0, (SELECT quantity FROM before) - @Quantity)
                WHERE show_id = @ShowId AND customer_sub = @CustomerSub
                RETURNING (SELECT quantity FROM before) < @Quantity;
            ";
            await using (var command = new NpgsqlCommand(restoreQuotaSql, connection, transaction))
            {
                command.Parameters.AddWithValue("Quantity", totalQuantity);
                command.Parameters.AddWithValue("ShowId", showId);
                command.Parameters.AddWithValue("CustomerSub", customerSub);
                var wasClamped = (bool)(await command.ExecuteScalarAsync())!;
                if (wasClamped)
                {
                    quotaClampCount++;
                }
            }
        }

        await transaction.CommitAsync();
        return new HoldReleaseSummary
        {
            HoldsReleased = expiredHolds.Count,
            TicketsReturned = totalTicketsReturned,
            QuotaClampCount = quotaClampCount
        };
    }

    private static async Task<HashSet<Guid>> GetKnownCategoryIdsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid showId, List<Guid> categoryIds)
    {
        const string sql = "SELECT category_id FROM stock WHERE show_id = @ShowId AND category_id = ANY(@CategoryIds);";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("ShowId", showId);
        command.Parameters.AddWithValue("CategoryIds", categoryIds.ToArray());

        await using var reader = await command.ExecuteReaderAsync();
        var known = new HashSet<Guid>();
        while (await reader.ReadAsync())
        {
            known.Add(reader.GetGuid(0));
        }

        return known;
    }

    public async Task<bool> CancelHoldAsync(Guid holdId, string customerSub, DateTimeOffset now)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        const string selectSql = @"
            SELECT show_id
            FROM holds
            WHERE id = @HoldId AND customer_sub = @CustomerSub AND status = 'Active';
        ";

        Guid showId;
        await using (var command = new NpgsqlCommand(selectSql, connection, transaction))
        {
            command.Parameters.AddWithValue("HoldId", holdId);
            command.Parameters.AddWithValue("CustomerSub", customerSub);
            var result = await command.ExecuteScalarAsync();
            if (result is null) return false;
            showId = (Guid)result;
        }

        const string updateHoldSql = "UPDATE holds SET status = 'Cancelled' WHERE id = @HoldId;";
        await using (var command = new NpgsqlCommand(updateHoldSql, connection, transaction))
        {
            command.Parameters.AddWithValue("HoldId", holdId);
            await command.ExecuteNonQueryAsync();
        }

        const string selectItemsSql = "SELECT category_id, quantity FROM hold_items WHERE hold_id = @HoldId;";
        var items = new List<(Guid CategoryId, int Quantity)>();
        await using (var command = new NpgsqlCommand(selectItemsSql, connection, transaction))
        {
            command.Parameters.AddWithValue("HoldId", holdId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                items.Add((reader.GetGuid(0), reader.GetInt32(1)));
            }
        }

        int totalQuantity = items.Sum(i => i.Quantity);

        foreach (var (categoryId, quantity) in items)
        {
            const string restoreStockSql = @"
                UPDATE stock
                SET available = available + @Quantity
                WHERE show_id = @ShowId AND category_id = @CategoryId;
            ";
            await using var command = new NpgsqlCommand(restoreStockSql, connection, transaction);
            command.Parameters.AddWithValue("Quantity", quantity);
            command.Parameters.AddWithValue("ShowId", showId);
            command.Parameters.AddWithValue("CategoryId", categoryId);
            await command.ExecuteNonQueryAsync();
        }

        const string restoreQuotaSql = @"
            UPDATE customer_quotas
            SET quantity = GREATEST(0, quantity - @TotalQuantity)
            WHERE show_id = @ShowId AND customer_sub = @CustomerSub;
        ";
        await using (var command = new NpgsqlCommand(restoreQuotaSql, connection, transaction))
        {
            command.Parameters.AddWithValue("TotalQuantity", totalQuantity);
            command.Parameters.AddWithValue("ShowId", showId);
            command.Parameters.AddWithValue("CustomerSub", customerSub);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return true;
    }

    public async Task<Hold?> GetActiveHoldForCustomerAsync(Guid showId, string customerSub, DateTimeOffset now)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string selectSql = @"
            SELECT id
            FROM holds
            WHERE show_id = @ShowId AND customer_sub = @CustomerSub AND status = 'Active' AND expires_at > @Now
            ORDER BY created_at DESC
            LIMIT 1;
        ";

        Guid? holdId = null;
        await using (var command = new NpgsqlCommand(selectSql, connection))
        {
            command.Parameters.AddWithValue("ShowId", showId);
            command.Parameters.AddWithValue("CustomerSub", customerSub);
            command.Parameters.AddWithValue("Now", now);

            var result = await command.ExecuteScalarAsync();
            if (result != null) holdId = (Guid)result;
        }

        if (holdId is null) return null;
        return await GetByIdAsync(holdId.Value);
    }
}
