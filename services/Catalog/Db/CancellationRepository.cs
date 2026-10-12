using Npgsql;

namespace Catalog.Service.Db;

public class CancellationRepository(DbConnectionFactory factory, TimeProvider clock)
{
    public async Task<(Guid ShowId, Guid Token)?> ClaimAsync()
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            UPDATE show_cancellations SET lease_until=@now+interval '2 minutes',lease_token=@token,attempts=attempts+1
            WHERE show_id=(SELECT show_id FROM show_cancellations WHERE NOT dispatched AND retry_at<=@now
              AND (lease_until IS NULL OR lease_until<@now) ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING show_id
            """, db);
        var token = Guid.CreateVersion7();
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        cmd.Parameters.AddWithValue("token", token);
        var id = await cmd.ExecuteScalarAsync();
        return id is Guid show ? (show, token) : null;
    }

    public async Task FinishAsync(Guid show, Guid token, bool salesStopped, bool dispatched)
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand("UPDATE show_cancellations SET sales_stopped=sales_stopped OR @stopped,dispatched=@done,lease_until=NULL,retry_at=@now+interval '15 seconds' WHERE show_id=@show AND lease_token=@token", db);
        cmd.Parameters.AddWithValue("show", show);
        cmd.Parameters.AddWithValue("token", token);
        cmd.Parameters.AddWithValue("stopped", salesStopped);
        cmd.Parameters.AddWithValue("done", dispatched);
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> SalesStoppedAsync(Guid show)
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT sales_stopped FROM show_cancellations WHERE show_id=@show", db);
        cmd.Parameters.AddWithValue("show", show);
        return await cmd.ExecuteScalarAsync() is true;
    }
}
