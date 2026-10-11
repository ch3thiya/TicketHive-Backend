using Npgsql;

namespace Booking.Service.Db;

public class ReminderEligibilityRepository(DbConnectionFactory connectionFactory) : IReminderEligibilityRepository
{
    public async Task<IReadOnlyList<ReminderOrder>> GetEligibleAsync(IReadOnlyCollection<Guid> orderIds)
    {
        var orders = new List<ReminderOrder>();
        if (orderIds.Count == 0)
        {
            return orders;
        }

        await using var connection = (NpgsqlConnection)await connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT o.id, COUNT(t.id)::int
            FROM orders o
            JOIN tickets t ON t.order_id = o.id AND t.voided_at IS NULL
            WHERE o.id = ANY(@OrderIds) AND o.status = 'Confirmed'
            GROUP BY o.id;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("OrderIds", orderIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            orders.Add(new ReminderOrder(reader.GetGuid(0), reader.GetInt32(1)));
        }

        return orders;
    }
}
