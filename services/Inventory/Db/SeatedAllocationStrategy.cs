using Inventory.Service.Models;
using Npgsql;
using NpgsqlTypes;

namespace Inventory.Service.Db;

public record SeatClaim(IReadOnlyList<HoldSeat> Seats, IReadOnlyList<HoldSeat> Unavailable);

// Seated stock behind the allocation seam (ADR-027). The stock counter is decremented exactly like
// the GA strategy does, but only for Seated stock, and the exact seats are locked and claimed.
public class SeatedAllocationStrategy : IAllocationStrategy
{
    public async Task<AllocatedStock?> TryAllocateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid showId, Guid categoryId, int quantity)
    {
        const string sql = @"
            UPDATE stock SET available = available - @Quantity
             WHERE show_id = @ShowId AND category_id = @CategoryId AND available >= @Quantity
               AND allocation_mode = 'Seated'
            RETURNING unit_price, currency;
        ";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("ShowId", showId);
        command.Parameters.AddWithValue("CategoryId", categoryId);
        command.Parameters.AddWithValue("Quantity", quantity);

        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? new AllocatedStock(reader.GetDecimal(0), reader.GetString(1).Trim()) : null;
    }

    // Locks the requested seats in id order (the same order for every caller, so concurrent
    // selections cannot deadlock) and reports every seat that is missing or not Available.
    // Seats that are not Available are owned by another hold, so refusing here, before any stock is
    // touched, also keeps this hold clear of that hold's own lifecycle updates.
    public async Task<SeatClaim> LockSeatsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid showId, IReadOnlyList<Guid> seatIds)
    {
        const string sql = @"
            SELECT id, category_id, section, row_label, seat_number, status
            FROM show_seats
            WHERE show_id = @ShowId AND id = ANY(@Ids)
            ORDER BY id
            FOR UPDATE;
        ";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("ShowId", showId);
        command.Parameters.Add(new NpgsqlParameter("Ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = seatIds.ToArray() });

        var available = new List<HoldSeat>();
        var unavailable = new List<HoldSeat>();
        var found = new HashSet<Guid>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var seat = new HoldSeat(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4));
                found.Add(seat.SeatId);
                (reader.GetString(5) == "Available" ? available : unavailable).Add(seat);
            }
        }

        unavailable.AddRange(seatIds.Where(id => !found.Contains(id)).Select(id => new HoldSeat(id, Guid.Empty, string.Empty, string.Empty, 0)));
        return new SeatClaim(available, unavailable);
    }

    public async Task MarkHeldAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid holdId, IReadOnlyList<Guid> seatIds)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE show_seats SET status = 'Held', hold_id = @HoldId WHERE id = ANY(@Ids) AND status = 'Available'", connection, transaction);
        command.Parameters.AddWithValue("HoldId", holdId);
        command.Parameters.Add(new NpgsqlParameter("Ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = seatIds.ToArray() });
        if (await command.ExecuteNonQueryAsync() != seatIds.Count)
        {
            // Unreachable while the seats are locked; refuse rather than hold a partial selection.
            throw new InvalidOperationException("A locked seat changed state while being held.");
        }
    }
}
