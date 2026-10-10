using Npgsql;
using Payment.Service.Models;

namespace Payment.Service.Db;

public class RefundRepository(DbConnectionFactory factory, TimeProvider clock)
{
    public async Task<RefundResponse> RefundAsync(Guid id, RefundRequest request)
    {
        if (request.Amount < 0 || request.Currency != "LKR") throw new ArgumentException("Invalid refund amount or currency.");
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
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
