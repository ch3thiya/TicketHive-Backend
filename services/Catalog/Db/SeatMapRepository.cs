using System.Text.Json;
using Catalog.Service.Models;
using Npgsql;
using NpgsqlTypes;

namespace Catalog.Service.Db;

public class SeatMapRepository(DbConnectionFactory connectionFactory) : ISeatMapRepository
{
    private const string RequestColumns =
        "id, show_id, organizer_id, description, status, rejection_reason, requested_at, decided_at, decided_by";

    private const string LayoutColumns =
        "id, show_id, request_id, version, status, layout, seat_count, created_by, created_at, enabled_at";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CreateSeatMapRequestOutcome> CreateRequestAsync(SeatMapRequest request)
    {
        await using var connection = (NpgsqlConnection)await connectionFactory.CreateConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var lockShow = new NpgsqlCommand("SELECT seating_mode FROM shows WHERE id = @ShowId FOR UPDATE", connection, transaction))
        {
            lockShow.Parameters.AddWithValue("ShowId", request.ShowId);
            var mode = await lockShow.ExecuteScalarAsync() as string;
            if (mode is null)
            {
                return CreateSeatMapRequestOutcome.ShowNotFound;
            }

            if (mode == SeatingModes.Seated)
            {
                return CreateSeatMapRequestOutcome.AlreadySeated;
            }
        }

        try
        {
            await using var insert = new NpgsqlCommand(@"
                INSERT INTO seat_map_requests (id, show_id, organizer_id, description, status, requested_at)
                VALUES (@Id, @ShowId, @OrganizerId, @Description, 'Pending', @RequestedAt)", connection, transaction);
            insert.Parameters.AddWithValue("Id", request.Id);
            insert.Parameters.AddWithValue("ShowId", request.ShowId);
            insert.Parameters.AddWithValue("OrganizerId", request.OrganizerId);
            insert.Parameters.AddWithValue("Description", request.Description);
            insert.Parameters.AddWithValue("RequestedAt", request.RequestedAt);
            await insert.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return CreateSeatMapRequestOutcome.Created;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return CreateSeatMapRequestOutcome.PendingExists;
        }
    }

    public async Task<SeatMapRequest?> GetRequestAsync(Guid requestId)
    {
        await using var connection = (NpgsqlConnection)await connectionFactory.CreateConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT {RequestColumns} FROM seat_map_requests WHERE id = @Id", connection);
        command.Parameters.AddWithValue("Id", requestId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapRequest(reader) : null;
    }

    public async Task<List<SeatMapRequest>> GetRequestsForShowAsync(Guid showId)
    {
        await using var connection = (NpgsqlConnection)await connectionFactory.CreateConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT {RequestColumns} FROM seat_map_requests WHERE show_id = @ShowId ORDER BY requested_at DESC, id DESC", connection);
        command.Parameters.AddWithValue("ShowId", showId);
        return await ReadRequestsAsync(command);
    }

    public async Task<List<SeatMapRequest>> ListRequestsAsync(string? status, int limit)
    {
        await using var connection = (NpgsqlConnection)await connectionFactory.CreateConnectionAsync();
        await using var command = new NpgsqlCommand($@"
            SELECT {RequestColumns} FROM seat_map_requests
            WHERE @Status::text IS NULL OR status = @Status
            ORDER BY requested_at DESC, id DESC LIMIT @Limit", connection);
        command.Parameters.Add(new NpgsqlParameter("Status", NpgsqlDbType.Text) { Value = (object?)status ?? DBNull.Value });
        command.Parameters.AddWithValue("Limit", limit);
        return await ReadRequestsAsync(command);
    }

    public async Task<List<SeatLayout>> GetLayoutsForShowAsync(Guid showId)
    {
        await using var connection = (NpgsqlConnection)await connectionFactory.CreateConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT {LayoutColumns} FROM seat_layouts WHERE show_id = @ShowId ORDER BY version DESC", connection);
        command.Parameters.AddWithValue("ShowId", showId);
        await using var reader = await command.ExecuteReaderAsync();
        var layouts = new List<SeatLayout>();
        while (await reader.ReadAsync())
        {
            layouts.Add(MapLayout(reader));
        }

        return layouts;
    }

    public async Task<SeatLayout?> GetLayoutAsync(Guid layoutId)
    {
        await using var connection = (NpgsqlConnection)await connectionFactory.CreateConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT {LayoutColumns} FROM seat_layouts WHERE id = @Id", connection);
        command.Parameters.AddWithValue("Id", layoutId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapLayout(reader) : null;
    }

    public async Task<(CreateDraftOutcome Outcome, SeatLayout? Layout)> CreateDraftAsync(
        Guid requestId, SeatLayoutSpec spec, string createdBy, DateTimeOffset now)
    {
        await using var connection = (NpgsqlConnection)await connectionFactory.CreateConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        Guid showId;
        await using (var lockRequest = new NpgsqlCommand("SELECT show_id FROM seat_map_requests WHERE id = @Id", connection, transaction))
        {
            lockRequest.Parameters.AddWithValue("Id", requestId);
            if (await lockRequest.ExecuteScalarAsync() is not Guid found)
            {
                return (CreateDraftOutcome.RequestNotFound, null);
            }

            showId = found;
        }

        // Show row first, then request: the same order every writer uses, so they cannot deadlock.
        await using (var lockShow = new NpgsqlCommand("SELECT 1 FROM shows WHERE id = @ShowId FOR UPDATE", connection, transaction))
        {
            lockShow.Parameters.AddWithValue("ShowId", showId);
            await lockShow.ExecuteScalarAsync();
        }

        await using (var status = new NpgsqlCommand("SELECT status FROM seat_map_requests WHERE id = @Id FOR UPDATE", connection, transaction))
        {
            status.Parameters.AddWithValue("Id", requestId);
            var requestState = (string?)await status.ExecuteScalarAsync();
            // A Completed request may take a new version while the layout is still unlocked (replacing it).
            if (requestState != SeatMapStatuses.Pending && requestState != SeatMapStatuses.Completed)
            {
                return (CreateDraftOutcome.RequestNotPending, null);
            }
        }

        int version;
        await using (var next = new NpgsqlCommand("SELECT COALESCE(MAX(version), 0) + 1 FROM seat_layouts WHERE show_id = @ShowId", connection, transaction))
        {
            next.Parameters.AddWithValue("ShowId", showId);
            version = Convert.ToInt32(await next.ExecuteScalarAsync());
        }

        var layout = new SeatLayout(Guid.CreateVersion7(), showId, requestId, version, SeatLayoutStatuses.Draft, spec,
            SeatLayoutRules.SeatCount(spec), createdBy, now, null);
        await using (var insert = new NpgsqlCommand(@"
            INSERT INTO seat_layouts (id, show_id, request_id, version, status, layout, seat_count, created_by, created_at)
            VALUES (@Id, @ShowId, @RequestId, @Version, 'Draft', @Layout, @SeatCount, @CreatedBy, @CreatedAt)", connection, transaction))
        {
            insert.Parameters.AddWithValue("Id", layout.Id);
            insert.Parameters.AddWithValue("ShowId", showId);
            insert.Parameters.AddWithValue("RequestId", requestId);
            insert.Parameters.AddWithValue("Version", version);
            insert.Parameters.Add(new NpgsqlParameter("Layout", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(spec, Json) });
            insert.Parameters.AddWithValue("SeatCount", layout.SeatCount);
            insert.Parameters.AddWithValue("CreatedBy", createdBy);
            insert.Parameters.AddWithValue("CreatedAt", now);
            await insert.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return (CreateDraftOutcome.Created, layout);
    }

    public async Task<CompleteEnableOutcome> CompleteEnableAsync(Guid layoutId, string decidedBy, DateTimeOffset now)
    {
        await using var connection = (NpgsqlConnection)await connectionFactory.CreateConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        Guid showId;
        Guid requestId;
        await using (var find = new NpgsqlCommand("SELECT show_id, request_id FROM seat_layouts WHERE id = @Id", connection, transaction))
        {
            find.Parameters.AddWithValue("Id", layoutId);
            await using var reader = await find.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return CompleteEnableOutcome.LayoutNotFound;
            }

            showId = reader.GetGuid(0);
            requestId = reader.GetGuid(1);
        }

        await using (var lockShow = new NpgsqlCommand("SELECT 1 FROM shows WHERE id = @ShowId FOR UPDATE", connection, transaction))
        {
            lockShow.Parameters.AddWithValue("ShowId", showId);
            await lockShow.ExecuteScalarAsync();
        }

        string layoutStatus;
        await using (var status = new NpgsqlCommand("SELECT status FROM seat_layouts WHERE id = @Id FOR UPDATE", connection, transaction))
        {
            status.Parameters.AddWithValue("Id", layoutId);
            layoutStatus = (string)(await status.ExecuteScalarAsync())!;
        }

        if (layoutStatus == SeatLayoutStatuses.Enabled)
        {
            return CompleteEnableOutcome.AlreadyEnabled;
        }

        if (layoutStatus != SeatLayoutStatuses.Draft)
        {
            return CompleteEnableOutcome.NotEnableable;
        }

        await using (var requestStatus = new NpgsqlCommand("SELECT status FROM seat_map_requests WHERE id = @Id FOR UPDATE", connection, transaction))
        {
            requestStatus.Parameters.AddWithValue("Id", requestId);
            var requestState = (string?)await requestStatus.ExecuteScalarAsync();
            if (requestState != SeatMapStatuses.Pending && requestState != SeatMapStatuses.Completed)
            {
                return CompleteEnableOutcome.NotEnableable;
            }
        }

        await using (var supersede = new NpgsqlCommand(
            "UPDATE seat_layouts SET status = 'Superseded' WHERE show_id = @ShowId AND status = 'Enabled'", connection, transaction))
        {
            supersede.Parameters.AddWithValue("ShowId", showId);
            await supersede.ExecuteNonQueryAsync();
        }

        await using (var enable = new NpgsqlCommand("UPDATE seat_layouts SET status = 'Enabled', enabled_at = @Now WHERE id = @Id", connection, transaction))
        {
            enable.Parameters.AddWithValue("Id", layoutId);
            enable.Parameters.AddWithValue("Now", now);
            await enable.ExecuteNonQueryAsync();
        }

        await using (var complete = new NpgsqlCommand(
            "UPDATE seat_map_requests SET status = 'Completed', decided_at = @Now, decided_by = @By WHERE id = @Id AND status = 'Pending'", connection, transaction))
        {
            complete.Parameters.AddWithValue("Id", requestId);
            complete.Parameters.AddWithValue("Now", now);
            complete.Parameters.AddWithValue("By", decidedBy);
            await complete.ExecuteNonQueryAsync();
        }

        await using (var seated = new NpgsqlCommand("UPDATE shows SET seating_mode = 'Seated' WHERE id = @ShowId", connection, transaction))
        {
            seated.Parameters.AddWithValue("ShowId", showId);
            await seated.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return CompleteEnableOutcome.Enabled;
    }

    public async Task<RejectOutcome> RejectAsync(Guid requestId, string reason, string decidedBy, DateTimeOffset now)
    {
        await using var connection = (NpgsqlConnection)await connectionFactory.CreateConnectionAsync();
        await using var command = new NpgsqlCommand(@"
            UPDATE seat_map_requests SET status = 'Rejected', rejection_reason = @Reason, decided_at = @Now, decided_by = @By
            WHERE id = @Id AND status = 'Pending'", connection);
        command.Parameters.AddWithValue("Id", requestId);
        command.Parameters.AddWithValue("Reason", reason);
        command.Parameters.AddWithValue("Now", now);
        command.Parameters.AddWithValue("By", decidedBy);
        if (await command.ExecuteNonQueryAsync() == 1)
        {
            return RejectOutcome.Rejected;
        }

        return await GetRequestAsync(requestId) is null ? RejectOutcome.NotFound : RejectOutcome.NotPending;
    }

    private static async Task<List<SeatMapRequest>> ReadRequestsAsync(NpgsqlCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync();
        var requests = new List<SeatMapRequest>();
        while (await reader.ReadAsync())
        {
            requests.Add(MapRequest(reader));
        }

        return requests;
    }

    private static SeatMapRequest MapRequest(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetGuid(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetFieldValue<DateTimeOffset>(6),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
        reader.IsDBNull(8) ? null : reader.GetString(8));

    private static SeatLayout MapLayout(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetGuid(2),
        reader.GetInt32(3),
        reader.GetString(4),
        JsonSerializer.Deserialize<SeatLayoutSpec>(reader.GetString(5), Json)!,
        reader.GetInt32(6),
        reader.GetString(7),
        reader.GetFieldValue<DateTimeOffset>(8),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9));
}
