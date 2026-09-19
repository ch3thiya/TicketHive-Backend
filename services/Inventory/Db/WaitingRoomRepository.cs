using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using Inventory.Service.Models;

namespace Inventory.Service.Db;

public class WaitingRoomRepository : IWaitingRoomRepository
{
    private readonly DbConnectionFactory _connectionFactory;

    public WaitingRoomRepository(DbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<WaitingRoomEntry> JoinQueueAsync(Guid showId, string customerSub, DateTimeOffset now)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        // Check if an existing entry exists for this show and customer
        const string selectSql = @"
            SELECT id, show_id, customer_sub, status, position, admission_token, admitted_at, token_expires_at, created_at, updated_at
            FROM waiting_room_entries
            WHERE show_id = @ShowId AND customer_sub = @CustomerSub;
        ";

        await using (var command = new NpgsqlCommand(selectSql, connection))
        {
            command.Parameters.AddWithValue("ShowId", showId);
            command.Parameters.AddWithValue("CustomerSub", customerSub);

            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var existing = MapEntry(reader);
                await reader.CloseAsync();

                // If currently active waiting or valid admitted token, return existing entry
                if (existing.Status == WaitingRoomStatus.Waiting)
                {
                    return existing;
                }

                if (existing.Status == WaitingRoomStatus.Admitted && existing.TokenExpiresAt.HasValue && existing.TokenExpiresAt.Value > now)
                {
                    return existing;
                }
            }
        }

        // Calculate next queue position
        const string maxPosSql = @"
            SELECT COALESCE(MAX(position), 0) + 1
            FROM waiting_room_entries
            WHERE show_id = @ShowId;
        ";

        int nextPos;
        await using (var posCmd = new NpgsqlCommand(maxPosSql, connection))
        {
            posCmd.Parameters.AddWithValue("ShowId", showId);
            nextPos = Convert.ToInt32(await posCmd.ExecuteScalarAsync());
        }

        var entryId = Guid.CreateVersion7();
        const string upsertSql = @"
            INSERT INTO waiting_room_entries (id, show_id, customer_sub, status, position, admission_token, admitted_at, token_expires_at, created_at, updated_at)
            VALUES (@Id, @ShowId, @CustomerSub, 'Waiting', @Position, NULL, NULL, NULL, @Now, @Now)
            ON CONFLICT (show_id, customer_sub) DO UPDATE SET
                status = 'Waiting',
                position = EXCLUDED.position,
                admission_token = NULL,
                admitted_at = NULL,
                token_expires_at = NULL,
                updated_at = EXCLUDED.updated_at;
        ";

        await using (var upsertCmd = new NpgsqlCommand(upsertSql, connection))
        {
            upsertCmd.Parameters.AddWithValue("Id", entryId);
            upsertCmd.Parameters.AddWithValue("ShowId", showId);
            upsertCmd.Parameters.AddWithValue("CustomerSub", customerSub);
            upsertCmd.Parameters.AddWithValue("Position", nextPos);
            upsertCmd.Parameters.AddWithValue("Now", now);

            await upsertCmd.ExecuteNonQueryAsync();
        }

        return (await GetStatusAsync(showId, customerSub, now))!;
    }

    public async Task<WaitingRoomEntry?> GetStatusAsync(Guid showId, string customerSub, DateTimeOffset now)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string selectSql = @"
            SELECT id, show_id, customer_sub, status, position, admission_token, admitted_at, token_expires_at, created_at, updated_at
            FROM waiting_room_entries
            WHERE show_id = @ShowId AND customer_sub = @CustomerSub;
        ";

        WaitingRoomEntry? entry = null;
        await using (var command = new NpgsqlCommand(selectSql, connection))
        {
            command.Parameters.AddWithValue("ShowId", showId);
            command.Parameters.AddWithValue("CustomerSub", customerSub);

            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                entry = MapEntry(reader);
            }
        }

        if (entry is null) return null;

        // Check token expiration if Admitted
        if (entry.Status == WaitingRoomStatus.Admitted && entry.TokenExpiresAt.HasValue && entry.TokenExpiresAt.Value <= now)
        {
            entry.Status = WaitingRoomStatus.Expired;
            entry.UpdatedAt = now;

            const string updateSql = @"
                UPDATE waiting_room_entries
                SET status = 'Expired', updated_at = @Now
                WHERE id = @Id;
            ";

            await using var updateCmd = new NpgsqlCommand(updateSql, connection);
            updateCmd.Parameters.AddWithValue("Now", now);
            updateCmd.Parameters.AddWithValue("Id", entry.Id);
            await updateCmd.ExecuteNonQueryAsync();
        }

        return entry;
    }

    public async Task<int> GetTotalWaitingAsync(Guid showId)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT COUNT(*)
            FROM waiting_room_entries
            WHERE show_id = @ShowId AND status = 'Waiting';
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ShowId", showId);

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public async Task<List<WaitingRoomEntry>> AdmitNextCustomersAsync(Guid showId, int batchSize, int tokenDurationMinutes, DateTimeOffset now)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        const string selectWaitingSql = @"
            SELECT id, customer_sub
            FROM waiting_room_entries
            WHERE show_id = @ShowId AND status = 'Waiting'
            ORDER BY position ASC, created_at ASC
            LIMIT @BatchSize;
        ";

        var toAdmit = new List<(Guid Id, string CustomerSub)>();
        await using (var command = new NpgsqlCommand(selectWaitingSql, connection, transaction))
        {
            command.Parameters.AddWithValue("ShowId", showId);
            command.Parameters.AddWithValue("BatchSize", batchSize);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                toAdmit.Add((reader.GetGuid(0), reader.GetString(1)));
            }
        }

        var admittedEntries = new List<WaitingRoomEntry>();
        var expiresAt = now.AddMinutes(tokenDurationMinutes);

        foreach (var (id, customerSub) in toAdmit)
        {
            var token = Guid.NewGuid().ToString("N");

            const string updateSql = @"
                UPDATE waiting_room_entries
                SET status = 'Admitted', admission_token = @Token, admitted_at = @Now, token_expires_at = @ExpiresAt, updated_at = @Now
                WHERE id = @Id;
            ";

            await using (var updateCmd = new NpgsqlCommand(updateSql, connection, transaction))
            {
                updateCmd.Parameters.AddWithValue("Token", token);
                updateCmd.Parameters.AddWithValue("Now", now);
                updateCmd.Parameters.AddWithValue("ExpiresAt", expiresAt);
                updateCmd.Parameters.AddWithValue("Id", id);
                await updateCmd.ExecuteNonQueryAsync();
            }

            admittedEntries.Add(new WaitingRoomEntry
            {
                Id = id,
                ShowId = showId,
                CustomerSub = customerSub,
                Status = WaitingRoomStatus.Admitted,
                Position = 0,
                AdmissionToken = token,
                AdmittedAt = now,
                TokenExpiresAt = expiresAt,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await transaction.CommitAsync();
        return admittedEntries;
    }

    public async Task<bool> ValidateAdmissionTokenAsync(Guid showId, string customerSub, string admissionToken, DateTimeOffset now)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string selectSql = @"
            SELECT id, token_expires_at
            FROM waiting_room_entries
            WHERE show_id = @ShowId AND customer_sub = @CustomerSub AND admission_token = @AdmissionToken AND status = 'Admitted';
        ";

        Guid? id = null;
        DateTimeOffset? tokenExpiresAt = null;

        await using (var command = new NpgsqlCommand(selectSql, connection))
        {
            command.Parameters.AddWithValue("ShowId", showId);
            command.Parameters.AddWithValue("CustomerSub", customerSub);
            command.Parameters.AddWithValue("AdmissionToken", admissionToken);

            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                id = reader.GetGuid(0);
                tokenExpiresAt = reader.GetFieldValue<DateTimeOffset>(1);
            }
        }

        if (id is null || !tokenExpiresAt.HasValue) return false;

        if (tokenExpiresAt.Value > now)
        {
            return true;
        }

        // Expired token
        const string updateSql = @"
            UPDATE waiting_room_entries
            SET status = 'Expired', updated_at = @Now
            WHERE id = @Id;
        ";

        await using var updateCmd = new NpgsqlCommand(updateSql, connection);
        updateCmd.Parameters.AddWithValue("Now", now);
        updateCmd.Parameters.AddWithValue("Id", id.Value);
        await updateCmd.ExecuteNonQueryAsync();

        return false;
    }

    public async Task<int> GetActiveAdmissionsCountAsync(Guid showId, DateTimeOffset now)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT COUNT(*)
            FROM waiting_room_entries
            WHERE show_id = @ShowId 
              AND status = 'Admitted' 
              AND token_expires_at > @Now;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ShowId", showId);
        command.Parameters.AddWithValue("Now", now);

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public async Task ConsumeAdmissionTokenAsync(Guid showId, string customerSub, string admissionToken, DateTimeOffset now)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            UPDATE waiting_room_entries
            SET status = 'Used', updated_at = @Now
            WHERE show_id = @ShowId AND customer_sub = @CustomerSub AND admission_token = @AdmissionToken;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ShowId", showId);
        command.Parameters.AddWithValue("CustomerSub", customerSub);
        command.Parameters.AddWithValue("AdmissionToken", admissionToken);
        command.Parameters.AddWithValue("Now", now);

        await command.ExecuteNonQueryAsync();
    }


    private static WaitingRoomEntry MapEntry(NpgsqlDataReader reader)
    {
        return new WaitingRoomEntry
        {
            Id = reader.GetGuid(0),
            ShowId = reader.GetGuid(1),
            CustomerSub = reader.GetString(2),
            Status = Enum.Parse<WaitingRoomStatus>(reader.GetString(3)),
            Position = reader.GetInt32(4),
            AdmissionToken = reader.IsDBNull(5) ? null : reader.GetString(5),
            AdmittedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            TokenExpiresAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(8),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(9)
        };
    }
}
