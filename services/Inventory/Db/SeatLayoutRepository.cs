using Inventory.Service.Models;
using Npgsql;
using NpgsqlTypes;

namespace Inventory.Service.Db;

public class SeatLayoutRepository(DbConnectionFactory connectionFactory) : ISeatLayoutRepository
{
    // Active and PaymentPending holds own stock until they expire; Converted holds are sold.
    private const string OpenHoldsCondition =
        "EXISTS (SELECT 1 FROM holds WHERE show_id = @ShowId AND status IN ('Active', 'PaymentPending', 'Converted'))";

    public async Task<EnableSeatsResult> EnableAsync(Guid showId, int version, IReadOnlyList<SeatToInitialize> seats, DateTimeOffset now)
    {
        await using var connection = (NpgsqlConnection)await connectionFactory.CreateConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // Seats before stock, the same order a new hold uses (ADR-027). Replacing an existing layout
        // therefore waits for in-flight holds on its seats instead of deadlocking with them.
        await using (var existingLayout = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM show_seat_layouts WHERE show_id = @ShowId)", connection, transaction))
        {
            existingLayout.Parameters.AddWithValue("ShowId", showId);
            if ((bool)(await existingLayout.ExecuteScalarAsync())!)
            {
                await using var lockSeats = new NpgsqlCommand(
                    "SELECT id FROM show_seats WHERE show_id = @ShowId ORDER BY id FOR UPDATE", connection, transaction);
                lockSeats.Parameters.AddWithValue("ShowId", showId);
                await lockSeats.ExecuteNonQueryAsync();
            }
        }

        var capacities = new Dictionary<Guid, int>();
        var anySold = false;
        await using (var lockStock = new NpgsqlCommand(
            "SELECT category_id, capacity, sold FROM stock WHERE show_id = @ShowId ORDER BY category_id FOR UPDATE", connection, transaction))
        {
            lockStock.Parameters.AddWithValue("ShowId", showId);
            await using var reader = await lockStock.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                capacities[reader.GetGuid(0)] = reader.GetInt32(1);
                anySold |= reader.GetInt32(2) > 0;
            }
        }

        if (capacities.Count == 0)
        {
            return new EnableSeatsResult(EnableSeatsOutcome.StockMissing, "The show has no stock yet.");
        }

        int? current = null;
        await using (var layout = new NpgsqlCommand("SELECT version FROM show_seat_layouts WHERE show_id = @ShowId FOR UPDATE", connection, transaction))
        {
            layout.Parameters.AddWithValue("ShowId", showId);
            if (await layout.ExecuteScalarAsync() is int existing)
            {
                current = existing;
            }
        }

        if (current == version)
        {
            return new EnableSeatsResult(EnableSeatsOutcome.AlreadyEnabled);
        }

        if (current > version)
        {
            return new EnableSeatsResult(EnableSeatsOutcome.StaleVersion, "A newer layout version is already enabled.");
        }

        bool locked;
        await using (var holds = new NpgsqlCommand($"SELECT {OpenHoldsCondition}", connection, transaction))
        {
            holds.Parameters.AddWithValue("ShowId", showId);
            locked = anySold || (bool)(await holds.ExecuteScalarAsync())!;
        }

        if (!locked && current is not null)
        {
            await using var notAvailable = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM show_seats WHERE show_id = @ShowId AND status <> 'Available')", connection, transaction);
            notAvailable.Parameters.AddWithValue("ShowId", showId);
            locked = (bool)(await notAvailable.ExecuteScalarAsync())!;
        }

        if (locked)
        {
            return new EnableSeatsResult(EnableSeatsOutcome.Locked, "Seats have been held or sold for this show, so its layout can no longer change.");
        }

        var perCategory = seats.GroupBy(s => s.CategoryId).ToDictionary(g => g.Key, g => g.Count());
        if (perCategory.Count != capacities.Count
            || perCategory.Any(p => !capacities.TryGetValue(p.Key, out var capacity) || capacity != p.Value))
        {
            return new EnableSeatsResult(EnableSeatsOutcome.Mismatch, "The seat count for each category must equal that category's capacity.");
        }

        if (seats.Select(s => (s.Section, s.Row, s.Number)).Distinct().Count() != seats.Count)
        {
            return new EnableSeatsResult(EnableSeatsOutcome.Mismatch, "The layout contains duplicate seats.");
        }

        if (current is not null)
        {
            // Every existing seat is Available (checked above) and no hold references seats yet.
            await using var clear = new NpgsqlCommand("DELETE FROM show_seats WHERE show_id = @ShowId", connection, transaction);
            clear.Parameters.AddWithValue("ShowId", showId);
            await clear.ExecuteNonQueryAsync();
        }

        await using (var insert = new NpgsqlCommand(@"
            INSERT INTO show_seats (id, show_id, category_id, section, row_label, seat_number, ordinal)
            SELECT s.id, @ShowId, s.category_id, s.section, s.row_label, s.seat_number, s.ordinal
            FROM unnest(@Ids, @Categories, @Sections, @Rows, @Numbers, @Ordinals) AS s(id, category_id, section, row_label, seat_number, ordinal)", connection, transaction))
        {
            insert.Parameters.AddWithValue("ShowId", showId);
            insert.Parameters.Add(new NpgsqlParameter("Ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = seats.Select(_ => Guid.CreateVersion7()).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("Categories", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = seats.Select(s => s.CategoryId).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("Sections", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = seats.Select(s => s.Section).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("Rows", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = seats.Select(s => s.Row).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("Numbers", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = seats.Select(s => s.Number).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("Ordinals", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = Enumerable.Range(0, seats.Count).ToArray() });
            await insert.ExecuteNonQueryAsync();
        }

        await using (var mode = new NpgsqlCommand("UPDATE stock SET allocation_mode = 'Seated' WHERE show_id = @ShowId", connection, transaction))
        {
            mode.Parameters.AddWithValue("ShowId", showId);
            await mode.ExecuteNonQueryAsync();
        }

        await using (var record = new NpgsqlCommand(@"
            INSERT INTO show_seat_layouts (show_id, version, seat_count, enabled_at) VALUES (@ShowId, @Version, @Count, @Now)
            ON CONFLICT (show_id) DO UPDATE SET version = EXCLUDED.version, seat_count = EXCLUDED.seat_count, enabled_at = EXCLUDED.enabled_at", connection, transaction))
        {
            record.Parameters.AddWithValue("ShowId", showId);
            record.Parameters.AddWithValue("Version", version);
            record.Parameters.AddWithValue("Count", seats.Count);
            record.Parameters.AddWithValue("Now", now);
            await record.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return new EnableSeatsResult(EnableSeatsOutcome.Enabled);
    }

    public async Task<SeatLockState> GetLockStateAsync(Guid showId)
    {
        await using var connection = (NpgsqlConnection)await connectionFactory.CreateConnectionAsync();

        int? version = null;
        await using (var layout = new NpgsqlCommand("SELECT version FROM show_seat_layouts WHERE show_id = @ShowId", connection))
        {
            layout.Parameters.AddWithValue("ShowId", showId);
            if (await layout.ExecuteScalarAsync() is int existing)
            {
                version = existing;
            }
        }

        await using var locked = new NpgsqlCommand($@"
            SELECT {OpenHoldsCondition}
                OR EXISTS (SELECT 1 FROM stock WHERE show_id = @ShowId AND sold > 0)
                OR EXISTS (SELECT 1 FROM show_seats WHERE show_id = @ShowId AND status <> 'Available')", connection);
        locked.Parameters.AddWithValue("ShowId", showId);
        var isLocked = (bool)(await locked.ExecuteScalarAsync())!;

        return new SeatLockState(version is not null, isLocked, version);
    }

    public async Task<SeatMapView?> GetSeatMapAsync(Guid showId)
    {
        await using var connection = (NpgsqlConnection)await connectionFactory.CreateConnectionAsync();

        int version;
        await using (var layout = new NpgsqlCommand("SELECT version FROM show_seat_layouts WHERE show_id = @ShowId", connection))
        {
            layout.Parameters.AddWithValue("ShowId", showId);
            if (await layout.ExecuteScalarAsync() is not int found)
            {
                return null;
            }

            version = found;
        }

        var categories = new List<SeatMapCategory>();
        await using (var stock = new NpgsqlCommand("SELECT category_id, unit_price, currency FROM stock WHERE show_id = @ShowId ORDER BY category_id", connection))
        {
            stock.Parameters.AddWithValue("ShowId", showId);
            await using var reader = await stock.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                categories.Add(new SeatMapCategory(reader.GetGuid(0), reader.GetDecimal(1), reader.GetString(2).Trim()));
            }
        }

        var seats = new List<SeatMapSeat>();
        await using (var command = new NpgsqlCommand(@"
            SELECT id, category_id, section, row_label, seat_number, status
            FROM show_seats WHERE show_id = @ShowId
            ORDER BY ordinal, section, row_label, seat_number", connection))
        {
            command.Parameters.AddWithValue("ShowId", showId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                seats.Add(new SeatMapSeat(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5)));
            }
        }

        return new SeatMapView(showId, version, categories, seats);
    }

    public async Task<IReadOnlyList<SeatState>?> GetUnavailableSeatsAsync(Guid showId)
    {
        await using var connection = (NpgsqlConnection)await connectionFactory.CreateConnectionAsync();

        await using (var layout = new NpgsqlCommand("SELECT 1 FROM show_seat_layouts WHERE show_id = @ShowId", connection))
        {
            layout.Parameters.AddWithValue("ShowId", showId);
            if (await layout.ExecuteScalarAsync() is null)
            {
                return null;
            }
        }

        var states = new List<SeatState>();
        await using var command = new NpgsqlCommand("SELECT id, status FROM show_seats WHERE show_id = @ShowId AND status <> 'Available' ORDER BY id", connection);
        command.Parameters.AddWithValue("ShowId", showId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            states.Add(new SeatState(reader.GetGuid(0), reader.GetString(1)));
        }

        return states;
    }
}
