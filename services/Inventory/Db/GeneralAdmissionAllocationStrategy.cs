using System;
using System.Threading.Tasks;
using Npgsql;

namespace Inventory.Service.Db;

// GA stock is a single counter row per category, so allocation is one
// conditional UPDATE. Seated allocation (Sprint 3) will need to lock and
// claim individual show_seats rows instead — a different implementation of
// IAllocationStrategy, not a change to this one.
public class GeneralAdmissionAllocationStrategy : IAllocationStrategy
{
    public async Task<AllocatedStock?> TryAllocateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid showId,
        Guid categoryId,
        int quantity)
    {
        const string sql = @"
            UPDATE stock SET available = available - @Quantity
             WHERE show_id = @ShowId AND category_id = @CategoryId AND available >= @Quantity
             RETURNING unit_price, currency;
        ";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("ShowId", showId);
        command.Parameters.AddWithValue("CategoryId", categoryId);
        command.Parameters.AddWithValue("Quantity", quantity);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new AllocatedStock(reader.GetDecimal(0), reader.GetString(1).Trim());
    }
}
