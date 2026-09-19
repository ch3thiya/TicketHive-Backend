using Npgsql;
using WaitingRoom.Service.Models;

namespace WaitingRoom.Service.Db;

public class QueueRepository : IQueueRepository
{
    private readonly DbConnectionFactory _connectionFactory;

    public QueueRepository(DbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<Queue?> GetQueueAsync(Guid showId, CancellationToken cancellationToken = default)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            select show_id, on_sale_at, prequeue_opens_at, serving_number, next_number, admit_batch, admit_interval_seconds, status
            from queues
            where show_id = @ShowId;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ShowId", showId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new Queue(
            reader.GetGuid(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            Enum.Parse<QueueStatus>(reader.GetString(7)));
    }

    public async Task<QueueEntry?> GetEntryAsync(Guid showId, string customerSub, CancellationToken cancellationToken = default)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            select show_id, customer_sub, random_rank, queue_number, joined_at, admitted_at
            from queue_entries
            where show_id = @ShowId and customer_sub = @CustomerSub;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ShowId", showId);
        command.Parameters.AddWithValue("CustomerSub", customerSub);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadEntry(reader);
    }

    public async Task<QueueEntry> JoinPreQueueAsync(Guid showId, string customerSub, DateTimeOffset joinedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        // A single atomic upsert: a repeat join hits the composite primary
        // key and the DO UPDATE (a no-op, self-assigning show_id) exists only
        // so RETURNING still yields the untouched existing row.
        const string sql = @"
            insert into queue_entries (show_id, customer_sub, random_rank, queue_number, joined_at)
            values (@ShowId, @CustomerSub, random(), null, @JoinedAt)
            on conflict (show_id, customer_sub) do update set show_id = queue_entries.show_id
            returning show_id, customer_sub, random_rank, queue_number, joined_at, admitted_at;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ShowId", showId);
        command.Parameters.AddWithValue("CustomerSub", customerSub);
        command.Parameters.AddWithValue("JoinedAt", joinedAt);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadEntry(reader);
    }

    public async Task<QueueEntry> JoinPostSaleAsync(Guid showId, string customerSub, DateTimeOffset joinedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Locks the queue row so a concurrent post-sale join for a different
        // customer cannot read the same next_number (ADR-007's shape, applied
        // to number issuance instead of stock).
        const string lockSql = "select next_number from queues where show_id = @ShowId for update;";
        await using (var lockCommand = new NpgsqlCommand(lockSql, connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("ShowId", showId);
            await lockCommand.ExecuteScalarAsync(cancellationToken);
        }

        // xmax = 0 tells apart a fresh insert from the ON CONFLICT no-op, so
        // next_number only advances once per customer, never on a repeat join.
        const string upsertSql = @"
            insert into queue_entries (show_id, customer_sub, random_rank, queue_number, joined_at)
            select @ShowId, @CustomerSub, random(), q.next_number, @JoinedAt
            from queues q
            where q.show_id = @ShowId
            on conflict (show_id, customer_sub) do update set show_id = queue_entries.show_id
            returning show_id, customer_sub, random_rank, queue_number, joined_at, admitted_at, (xmax = 0) as inserted;
        ";

        QueueEntry entry;
        bool inserted;
        await using (var command = new NpgsqlCommand(upsertSql, connection, transaction))
        {
            command.Parameters.AddWithValue("ShowId", showId);
            command.Parameters.AddWithValue("CustomerSub", customerSub);
            command.Parameters.AddWithValue("JoinedAt", joinedAt);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            entry = ReadEntry(reader);
            inserted = reader.GetBoolean(6);
        }

        if (inserted)
        {
            const string incrementSql = "update queues set next_number = next_number + 1 where show_id = @ShowId;";
            await using var incrementCommand = new NpgsqlCommand(incrementSql, connection, transaction);
            incrementCommand.Parameters.AddWithValue("ShowId", showId);
            await incrementCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return entry;
    }

    public async Task RunOnSaleTransitionAsync(Guid showId, CancellationToken cancellationToken = default)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Transaction-scoped (not session-scoped) so the lock always releases
        // at commit or rollback, even if the underlying connection is later
        // handed back out from Npgsql's pool. Safe to attempt from any instance.
        const string lockSql = "select pg_try_advisory_xact_lock(hashtext(@ShowId));";
        bool lockAcquired;
        await using (var lockCommand = new NpgsqlCommand(lockSql, connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("ShowId", showId.ToString());
            lockAcquired = (bool)(await lockCommand.ExecuteScalarAsync(cancellationToken))!;
        }

        if (!lockAcquired)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        // Locking the row also blocks a concurrent post-sale join (which
        // takes the same row lock) until the transition finishes.
        const string statusSql = "select status from queues where show_id = @ShowId for update;";
        string? status;
        await using (var statusCommand = new NpgsqlCommand(statusSql, connection, transaction))
        {
            statusCommand.Parameters.AddWithValue("ShowId", showId);
            status = (string?)await statusCommand.ExecuteScalarAsync(cancellationToken);
        }

        // Idempotent: no queue, or already transitioned — nothing to do.
        if (status != nameof(QueueStatus.PreQueue))
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        const string assignSql = @"
            with ranked as (
                select customer_sub, row_number() over (order by random_rank) as rn
                from queue_entries
                where show_id = @ShowId and queue_number is null
            )
            update queue_entries qe
            set queue_number = ranked.rn
            from ranked
            where qe.show_id = @ShowId and qe.customer_sub = ranked.customer_sub;
        ";
        await using (var assignCommand = new NpgsqlCommand(assignSql, connection, transaction))
        {
            assignCommand.Parameters.AddWithValue("ShowId", showId);
            await assignCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string openSql = @"
            update queues
            set next_number = (select coalesce(max(queue_number), 0) from queue_entries where show_id = @ShowId) + 1,
                status = @OpenStatus
            where show_id = @ShowId;
        ";
        await using (var openCommand = new NpgsqlCommand(openSql, connection, transaction))
        {
            openCommand.Parameters.AddWithValue("ShowId", showId);
            openCommand.Parameters.AddWithValue("OpenStatus", nameof(QueueStatus.Open));
            await openCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public Task<long?> TryAdvanceServingNumberAsync(Guid showId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Added when the scheduler is implemented.");

    private static QueueEntry ReadEntry(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetDouble(2),
        reader.IsDBNull(3) ? null : reader.GetInt64(3),
        reader.GetFieldValue<DateTimeOffset>(4),
        reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5));
}
