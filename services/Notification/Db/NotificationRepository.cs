using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using Notification.Service.Models;

namespace Notification.Service.Db;

public class NotificationRepository : INotificationRepository
{
    private readonly DbConnectionFactory _connectionFactory;

    public NotificationRepository(DbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<NotificationRecord> CreateAsync(NotificationRecord record)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            INSERT INTO notifications (id, order_id, customer_email, subject, status, error_message, sent_at)
            VALUES (@Id, @OrderId, @CustomerEmail, @Subject, @Status, @ErrorMessage, @SentAt)
            ON CONFLICT (order_id, customer_email) DO NOTHING;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", record.Id);
        command.Parameters.AddWithValue("OrderId", record.OrderId);
        command.Parameters.AddWithValue("CustomerEmail", record.CustomerEmail);
        command.Parameters.AddWithValue("Subject", record.Subject);
        command.Parameters.AddWithValue("Status", record.Status);
        command.Parameters.AddWithValue("ErrorMessage", (object?)record.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("SentAt", record.SentAt);

        await command.ExecuteNonQueryAsync();
        return record;
    }

    public async Task<bool> ExistsForOrderAndEmailAsync(Guid orderId, string customerEmail)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT COUNT(1) FROM notifications
            WHERE order_id = @OrderId AND customer_email = @CustomerEmail AND status = 'Sent';
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("OrderId", orderId);
        command.Parameters.AddWithValue("CustomerEmail", customerEmail);

        var count = Convert.ToInt64(await command.ExecuteScalarAsync());
        return count > 0;
    }

    public async Task<IReadOnlyList<NotificationRecord>> GetByOrderIdAsync(Guid orderId)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT id, order_id, customer_email, subject, status, error_message, sent_at
            FROM notifications
            WHERE order_id = @OrderId
            ORDER BY sent_at DESC;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("OrderId", orderId);

        await using var reader = await command.ExecuteReaderAsync();
        var list = new List<NotificationRecord>();

        while (await reader.ReadAsync())
        {
            list.Add(new NotificationRecord
            {
                Id = reader.GetGuid(0),
                OrderId = reader.GetGuid(1),
                CustomerEmail = reader.GetString(2),
                Subject = reader.GetString(3),
                Status = reader.GetString(4),
                ErrorMessage = reader.IsDBNull(5) ? null : reader.GetString(5),
                SentAt = reader.GetFieldValue<DateTimeOffset>(6)
            });
        }

        return list;
    }
}
