using Npgsql;
using Payment.Service.Models;

namespace Payment.Service.Db;

public class RefundRepository(DbConnectionFactory factory, TimeProvider clock)
{
    public async Task<RefundResponse> RefundAsync(Guid id, RefundRequest request)
    {
        if (request.Amount < 0 || request.Currency != "LKR") throw new ArgumentException("Invalid refund amount or currency.");
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using (var payment = new NpgsqlCommand("SELECT amount,currency FROM payments WHERE order_id=@id AND status='Succeeded' ORDER BY updated_at DESC LIMIT 1", db))
        {
            payment.Parameters.AddWithValue("id", id);
            await using var paid = await payment.ExecuteReaderAsync();
            if (!await paid.ReadAsync()) throw new InvalidOperationException("A verified successful payment is required before refunding this order.");
            var paidAmount = paid.GetDecimal(0);
            var paidCurrency = paid.GetString(1).Trim();
            if (paidAmount != request.Amount || paidCurrency != request.Currency)
                throw new InvalidOperationException("Refund must match the verified amount and currency actually paid.");
        }
        await using var insert = new NpgsqlCommand("""
            INSERT INTO refunds VALUES(@id,@amount,@currency,'Simulated','Sandbox refund route is not configured; no money moved.',@now)
            ON CONFLICT DO NOTHING;
            """, db);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("amount", request.Amount);
        insert.Parameters.AddWithValue("currency", request.Currency);
        insert.Parameters.AddWithValue("now", clock.GetUtcNow());
        await insert.ExecuteNonQueryAsync();
        await using var read = new NpgsqlCommand("SELECT amount,currency,status,reason FROM refunds WHERE order_id=@id", db);
        read.Parameters.AddWithValue("id", id);
        await using var r = await read.ExecuteReaderAsync();
        await r.ReadAsync();
        if (r.GetDecimal(0) != request.Amount || r.GetString(1).Trim() != request.Currency)
            throw new InvalidOperationException("Refund operation key was reused with a different amount or currency.");
        return new(id, r.GetDecimal(0), r.GetString(1).Trim(), r.GetString(2), r.GetString(3));
    }
}
