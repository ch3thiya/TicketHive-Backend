using System.Data;
using Booking.Service.Models;
using Npgsql;

namespace Booking.Service.Db;

public class SalesRepository(DbConnectionFactory connectionFactory) : ISalesRepository
{
    public async Task<(IReadOnlyList<SoldRow> Sold, IReadOnlyList<UsedRow> Used)> GetAsync(Guid showId)
    {
        await using var connection = (NpgsqlConnection)await connectionFactory.CreateConnectionAsync();

        // One repeatable-read snapshot: both queries see the same committed state, so an order
        // confirmed (or cancelled) and scanned between them cannot skew sold against used.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);

        const string soldSql = @"
            SELECT oi.category_id, o.currency, SUM(oi.quantity)::int, SUM(oi.quantity * oi.unit_price)
            FROM orders o
            JOIN order_items oi ON oi.order_id = o.id
            WHERE o.show_id = @ShowId AND o.status = 'Confirmed'
            GROUP BY oi.category_id, o.currency
            ORDER BY oi.category_id;
        ";

        var sold = new List<SoldRow>();
        await using (var command = new NpgsqlCommand(soldSql, connection, transaction))
        {
            command.Parameters.AddWithValue("ShowId", showId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                sold.Add(new SoldRow(reader.GetGuid(0), reader.GetString(1).Trim(), reader.GetInt32(2), reader.GetDecimal(3)));
            }
        }

        const string usedSql = @"
            SELECT t.category_id, COUNT(*)::int
            FROM tickets t
            JOIN orders o ON o.id = t.order_id
            WHERE t.show_id = @ShowId AND o.status = 'Confirmed' AND t.voided_at IS NULL AND t.used_at IS NOT NULL
            GROUP BY t.category_id
            ORDER BY t.category_id;
        ";

        var used = new List<UsedRow>();
        await using (var command = new NpgsqlCommand(usedSql, connection, transaction))
        {
            command.Parameters.AddWithValue("ShowId", showId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                used.Add(new UsedRow(reader.GetGuid(0), reader.GetInt32(1)));
            }
        }

        await transaction.CommitAsync();
        return (sold, used);
    }
}
