using Notification.Service.Models;
using Npgsql;

namespace Notification.Service.Db;

public class EventReminderRepository(DbConnectionFactory factory, TimeProvider clock)
{
    // One row per order: a second event for the same order (Kafka redelivery) changes nothing.
    public async Task<bool> EnsureAsync(ReminderSeed seed, bool deliverable)
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO event_reminders
                (order_id, show_id, recipient_email, customer_name, ticket_count, status, next_attempt_at, created_at, updated_at)
            VALUES (@order, @show, @email, @name, @count, @status, @now, @now, @now)
            ON CONFLICT (order_id) DO NOTHING
            """, db);
        cmd.Parameters.AddWithValue("order", seed.OrderId);
        cmd.Parameters.AddWithValue("show", seed.ShowId);
        cmd.Parameters.AddWithValue("email", seed.RecipientEmail.Length > 255 ? seed.RecipientEmail[..255] : seed.RecipientEmail);
        cmd.Parameters.AddWithValue("name", seed.CustomerName.Length > 255 ? seed.CustomerName[..255] : seed.CustomerName);
        cmd.Parameters.AddWithValue("count", Math.Max(1, seed.TicketCount));
        cmd.Parameters.AddWithValue("status", deliverable ? ReminderStatuses.Pending : ReminderStatuses.MissingRecipient);
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    // Takes due pending reminders for this instance. SKIP LOCKED means any number of instances can
    // run the job and each reminder is handed to exactly one of them. A send that was started but
    // never finished has an unknown outcome, so it is parked for people, never sent again.
    public async Task<IReadOnlyList<ReminderRow>> ClaimDueAsync(int batchSize, int staleSendMinutes)
    {
        var now = clock.GetUtcNow();
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();

        await using (var recover = new NpgsqlCommand("""
            UPDATE event_reminders
               SET status = 'NeedsReconciliation', last_error = 'The send did not finish, so its outcome is unknown', updated_at = @now
             WHERE status = 'Sending' AND updated_at < @now - make_interval(mins => @stale)
            """, db))
        {
            recover.Parameters.AddWithValue("now", now);
            recover.Parameters.AddWithValue("stale", staleSendMinutes);
            await recover.ExecuteNonQueryAsync();
        }

        await using var claim = new NpgsqlCommand("""
            UPDATE event_reminders SET status = 'Sending', updated_at = @now
             WHERE order_id IN (
                 SELECT order_id FROM event_reminders
                  WHERE status = 'Pending' AND next_attempt_at <= @now
                  ORDER BY next_attempt_at, order_id
                  FOR UPDATE SKIP LOCKED LIMIT @batch)
            RETURNING order_id, show_id, recipient_email, customer_name, ticket_count, attempts
            """, db);
        claim.Parameters.AddWithValue("now", now);
        claim.Parameters.AddWithValue("batch", batchSize);

        var rows = new List<ReminderRow>();
        await using var reader = await claim.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ReminderRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5)));
        }

        return rows;
    }

    // Puts claimed reminders back to be looked at again later, without counting an attempt.
    public Task DeferAsync(IReadOnlyCollection<Guid> orderIds, DateTimeOffset until) => ExecuteAsync("""
        UPDATE event_reminders SET status = 'Pending', next_attempt_at = @until, updated_at = @now
         WHERE order_id = ANY(@ids) AND status = 'Sending'
        """, ("until", until), ("ids", orderIds.ToArray()));

    public Task SkipAsync(Guid orderId, string reason) => ExecuteAsync("""
        UPDATE event_reminders SET status = 'Skipped', skip_reason = @reason, updated_at = @now
         WHERE order_id = @id AND status = 'Sending'
        """, ("id", orderId), ("reason", reason));

    public Task CompleteAsync(Guid orderId, string status) => ExecuteAsync("""
        UPDATE event_reminders SET status = @status, sent_at = @now, last_error = NULL, updated_at = @now
         WHERE order_id = @id AND status = 'Sending'
        """, ("id", orderId), ("status", status));

    public Task MarkNeedsReconciliationAsync(Guid orderId, string error) => ExecuteAsync("""
        UPDATE event_reminders SET status = 'NeedsReconciliation', last_error = @error, updated_at = @now
         WHERE order_id = @id AND status = 'Sending'
        """, ("id", orderId), ("error", Truncate(error)));

    // A definite refusal counts an attempt. It goes back to Pending for another try until the attempts
    // are used up, and then stays Failed. The order and its tickets are never touched.
    // Null when the reminder was no longer claimed, so a stale worker changes nothing.
    public async Task<string?> RecordFailureAsync(Guid orderId, string error, int maxAttempts, DateTimeOffset retryAt)
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            UPDATE event_reminders
               SET attempts = attempts + 1,
                   status = CASE WHEN attempts + 1 < @max THEN 'Pending' ELSE 'Failed' END,
                   next_attempt_at = CASE WHEN attempts + 1 < @max THEN @retry ELSE next_attempt_at END,
                   last_error = @error, updated_at = @now
             WHERE order_id = @id AND status = 'Sending'
            RETURNING status
            """, db);
        cmd.Parameters.AddWithValue("id", orderId);
        cmd.Parameters.AddWithValue("max", maxAttempts);
        cmd.Parameters.AddWithValue("retry", retryAt);
        cmd.Parameters.AddWithValue("error", Truncate(error));
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        return (string?)await cmd.ExecuteScalarAsync();
    }

    public async Task<ReminderRecord?> GetAsync(Guid orderId)
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT order_id, status, skip_reason, attempts, next_attempt_at, last_error, sent_at
              FROM event_reminders WHERE order_id = @id
            """, db);
        cmd.Parameters.AddWithValue("id", orderId);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new ReminderRecord(
                reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3),
                reader.GetFieldValue<DateTimeOffset>(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6))
            : null;
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        cmd.Parameters.AddWithValue("now", clock.GetUtcNow());
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Truncate(string text) => text.Length > 500 ? text[..500] : text;
}
