using System.Text.Json;
using Notification.Service.Models;
using Npgsql;

namespace Notification.Service.Db;

public class CancellationEmailRepository(DbConnectionFactory factory, TimeProvider clock)
{
    public async Task<string> EnqueueAsync(CancellationEmail email)
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO cancellation_emails VALUES(@key,@payload::jsonb,'Pending',@now,@now) ON CONFLICT DO NOTHING;
            SELECT status FROM cancellation_emails WHERE operation_key=@key;
            """, db);
        cmd.Parameters.AddWithValue("key", email.Key);
        cmd.Parameters.AddWithValue("payload", JsonSerializer.Serialize(email));
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<CancellationEmail?> ClaimAsync()
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            UPDATE cancellation_emails SET status='NeedsReconciliation',updated_at=@now
              WHERE status='Sending' AND updated_at<@now-interval '5 minutes';
            UPDATE cancellation_emails SET status='Sending',updated_at=@now
              WHERE operation_key=(SELECT operation_key FROM cancellation_emails WHERE status='Pending'
                ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1)
              RETURNING payload::text;
            """, db);
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        return await cmd.ExecuteScalarAsync() is string json ? JsonSerializer.Deserialize<CancellationEmail>(json) : null;
    }

    public async Task CompleteAsync(string key, string status)
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand("UPDATE cancellation_emails SET status=@status,updated_at=@now WHERE operation_key=@key AND status='Sending'", db);
        cmd.Parameters.AddWithValue("key", key);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        await cmd.ExecuteNonQueryAsync();
    }
}
