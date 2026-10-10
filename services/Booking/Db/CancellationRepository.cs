using Booking.Service.Models;
using Npgsql;

namespace Booking.Service.Db;

public class CancellationRepository(DbConnectionFactory factory, TimeProvider clock)
{
    public async Task<object> ProgressAsync(Guid showId)
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT COUNT(*),COUNT(*) FILTER(WHERE c.refund_status='Succeeded'),
              COUNT(*) FILTER(WHERE c.refund_status='Simulated'),
              COUNT(*) FILTER(WHERE c.order_id IS NULL OR c.refund_status='Pending' OR NOT c.inventory_returned),
              COUNT(*) FILTER(WHERE c.notification_status NOT IN ('Sent','Simulated'))
            FROM orders o LEFT JOIN order_cancellations c ON c.order_id=o.id
            WHERE o.show_id=@show AND (o.status IN ('Confirmed','PaymentPending') OR c.reason='Show')
            """, db);
        cmd.Parameters.AddWithValue("show", showId);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return new { TotalOrders = r.GetInt64(0), Refunded = r.GetInt64(1), Simulated = r.GetInt64(2), Processing = r.GetInt64(3), NotificationsPending = r.GetInt64(4) };
    }

    public async Task<(Guid Id, Guid Token)?> ClaimAsync()
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            UPDATE order_cancellations SET lease_until=@now+interval '2 minutes',lease_token=@token,attempts=attempts+1
            WHERE order_id=(SELECT order_id FROM order_cancellations
              WHERE (refund_status='Pending' OR NOT inventory_returned) AND retry_at<=@now
              AND (lease_until IS NULL OR lease_until<@now) ORDER BY retry_at FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING order_id
            """, db);
        var token = Guid.CreateVersion7();
        cmd.Parameters.AddWithValue("token", token);
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        var id = await cmd.ExecuteScalarAsync();
        return id is Guid value ? (value, token) : null;
    }

    public async Task FinishAsync(Guid id, Guid token, string? refund, string? error)
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            UPDATE order_cancellations SET refund_status=COALESCE(@refund,refund_status),
              inventory_returned=CASE WHEN @error IS NULL THEN true ELSE inventory_returned END,
              last_error=@error,retry_at=@now+interval '15 seconds',lease_until=NULL
            WHERE order_id=@id AND lease_token=@token
            """, db);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("token", token);
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        cmd.Parameters.Add(new NpgsqlParameter("refund", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)refund ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("error", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<Booking.Service.Clients.CancellationEmail>> ReadyEmailsAsync()
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT CASE WHEN c.reason='Show' THEN 'show:'||o.show_id||':'||o.customer_sub ELSE 'order:'||o.id END AS key,
              MAX(NULLIF(o.customer_email,'')),MAX(NULLIF(o.customer_name,'')),o.show_id,array_agg(o.id ORDER BY o.id),
              SUM(CASE WHEN c.refund_status='NotRequired' THEN 0 ELSE o.total_amount END),MIN(o.currency),
              bool_or(c.refund_status='Simulated'),c.reason
            FROM order_cancellations c JOIN orders o ON o.id=c.order_id
            WHERE c.notification_status NOT IN ('Sent','Simulated')
              AND NOT EXISTS(SELECT 1 FROM orders pending WHERE pending.show_id=o.show_id AND c.reason='Show' AND pending.status IN ('Confirmed','PaymentPending'))
            GROUP BY key,o.show_id,c.reason
            HAVING bool_and(c.inventory_returned AND c.refund_status<>'Pending')
            LIMIT 100
            """, db);
        var result = new List<Booking.Service.Clients.CancellationEmail>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) result.Add(new(r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.GetGuid(3), r.GetFieldValue<Guid[]>(4), r.GetDecimal(5), r.GetString(6).Trim(), r.GetBoolean(7), r.GetString(8)));
        return result;
    }

    public async Task RecordEmailAsync(Guid[] ids, string status)
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand("UPDATE order_cancellations SET notification_status=@status WHERE order_id=ANY(@ids)", db);
        cmd.Parameters.AddWithValue("ids", ids);
        cmd.Parameters.AddWithValue("status", status);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<string?> CancelAsync(Guid id, string customer, DateTimeOffset startsAt, bool automatic)
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var tx = await db.BeginTransactionAsync();
        await using var read = new NpgsqlCommand("SELECT status,customer_sub FROM orders WHERE id=@id FOR UPDATE", db, tx);
        read.Parameters.AddWithValue("id", id);
        OrderStatus status;
        string owner;
        await using (var reader = await read.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) return "NotFound";
            status = Enum.Parse<OrderStatus>(reader.GetString(0));
            owner = reader.GetString(1);
        }
        await using var used = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM tickets WHERE order_id=@id AND used_at IS NOT NULL)", db, tx);
        used.Parameters.AddWithValue("id", id);
        var refusal = CancellationRules.Refusal(status, owner == customer, (bool)(await used.ExecuteScalarAsync())!, startsAt, clock.GetUtcNow(), automatic);
        if (refusal is not null) return refusal;
        if (status == OrderStatus.Cancelled) return null;
        await using var cancel = new NpgsqlCommand("""
            UPDATE orders SET status='Cancelled',updated_at=@now WHERE id=@id;
            UPDATE tickets SET voided_at=@now WHERE order_id=@id AND voided_at IS NULL;
            INSERT INTO order_cancellations(order_id,reason,refund_status,created_at,retry_at)
            VALUES(@id,@reason,@refund,@now,@now) ON CONFLICT DO NOTHING;
            """, db, tx);
        cancel.Parameters.AddWithValue("id", id);
        cancel.Parameters.AddWithValue("now", clock.GetUtcNow());
        cancel.Parameters.AddWithValue("reason", automatic ? "Show" : "Customer");
        cancel.Parameters.AddWithValue("refund", status == OrderStatus.Confirmed ? "Pending" : "NotRequired");
        await cancel.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return null;
    }

    public async Task CancelShowAsync(Guid showId)
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        // Serialize against order creation, then retain a tombstone for late creates.
        await using (var tx = await db.BeginTransactionAsync())
        {
            await using var close = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@show::text,169)); INSERT INTO cancelled_shows VALUES(@show,@now) ON CONFLICT DO NOTHING", db, tx);
            close.Parameters.AddWithValue("show", showId);
            close.Parameters.AddWithValue("now", clock.GetUtcNow());
            await close.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }
        await using var list = new NpgsqlCommand("SELECT id FROM orders WHERE show_id=@show AND status IN ('PaymentPending','Confirmed') ORDER BY id", db);
        list.Parameters.AddWithValue("show", showId);
        var ids = new List<Guid>();
        await using (var reader = await list.ExecuteReaderAsync())
            while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
        foreach (var id in ids) await CancelAsync(id, "", DateTimeOffset.MaxValue, true);
    }

    public async Task<CancellationState?> GetAsync(Guid id)
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT refund_status,inventory_returned,notification_status,reason FROM order_cancellations WHERE order_id=@id", db);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? new(id, r.GetString(0), r.GetBoolean(1), r.GetString(2), r.GetString(3)) : null;
    }
}
