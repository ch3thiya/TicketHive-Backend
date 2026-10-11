using Booking.Service.Models;
using Npgsql;

namespace Booking.Service.Db;

public class SalesRepository(DbConnectionFactory connectionFactory) : ISalesRepository
{
    public async Task<IReadOnlyList<SoldRow>> GetSoldAsync(Guid showId)
    {
        await using var connection = (NpgsqlConnection)await connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT oi.category_id, o.currency, SUM(oi.quantity)::int, SUM(oi.quantity * oi.unit_price)
            FROM orders o
            JOIN order_items oi ON oi.order_id = o.id
            WHERE o.show_id = @ShowId AND o.status = 'Confirmed'
            GROUP BY oi.category_id, o.currency
            ORDER BY oi.category_id;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ShowId", showId);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<SoldRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new SoldRow(reader.GetGuid(0), reader.GetString(1).Trim(), reader.GetInt32(2), reader.GetDecimal(3)));
        }

        return rows;
    }

    public async Task<IReadOnlyList<UsedRow>> GetUsedAsync(Guid showId)
    {
        await using var connection = (NpgsqlConnection)await connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT t.category_id, COUNT(*)::int
            FROM tickets t
            JOIN orders o ON o.id = t.order_id
            WHERE t.show_id = @ShowId AND o.status = 'Confirmed' AND t.voided_at IS NULL AND t.used_at IS NOT NULL
            GROUP BY t.category_id
            ORDER BY t.category_id;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ShowId", showId);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<UsedRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new UsedRow(reader.GetGuid(0), reader.GetInt32(1)));
        }

        return rows;
    }
}
