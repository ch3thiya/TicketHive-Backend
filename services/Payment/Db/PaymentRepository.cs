using System;
using System.Threading.Tasks;
using Payment.Service.Models;
using Npgsql;

namespace Payment.Service.Db;

public class PaymentRepository : IPaymentRepository
{
    private readonly DbConnectionFactory _connectionFactory;

    public PaymentRepository(DbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<PaymentTransaction> CreateAsync(PaymentTransaction transaction)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            INSERT INTO payments (id, order_id, customer_sub, amount, currency, status, payhere_payment_id, created_at, updated_at)
            VALUES (@Id, @OrderId, @CustomerSub, @Amount, @Currency, @Status, @PayHerePaymentId, @CreatedAt, @UpdatedAt);
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", transaction.Id);
        command.Parameters.AddWithValue("OrderId", transaction.OrderId);
        command.Parameters.AddWithValue("CustomerSub", transaction.CustomerSub);
        command.Parameters.AddWithValue("Amount", transaction.Amount);
        command.Parameters.AddWithValue("Currency", transaction.Currency);
        command.Parameters.AddWithValue("Status", transaction.Status.ToString());
        command.Parameters.AddWithValue("PayHerePaymentId", (object?)transaction.PayHerePaymentId ?? DBNull.Value);
        command.Parameters.AddWithValue("CreatedAt", transaction.CreatedAt);
        command.Parameters.AddWithValue("UpdatedAt", transaction.UpdatedAt);

        await command.ExecuteNonQueryAsync();
        return transaction;
    }

    public async Task<PaymentTransaction?> GetByOrderIdAsync(Guid orderId)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT id, order_id, customer_sub, amount, currency, status, payhere_payment_id, created_at, updated_at
            FROM payments
            WHERE order_id = @OrderId
            ORDER BY created_at DESC
            LIMIT 1;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("OrderId", orderId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new PaymentTransaction
        {
            Id = reader.GetGuid(0),
            OrderId = reader.GetGuid(1),
            CustomerSub = reader.GetString(2),
            Amount = reader.GetDecimal(3),
            Currency = reader.GetString(4).Trim(),
            Status = Enum.Parse<PaymentStatus>(reader.GetString(5)),
            PayHerePaymentId = reader.IsDBNull(6) ? null : reader.GetString(6),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(7),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(8)
        };
    }

    public async Task<bool> UpdateStatusAsync(Guid orderId, PaymentStatus status, string? payHerePaymentId, DateTimeOffset updatedAt)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            UPDATE payments
            SET status = @Status, payhere_payment_id = COALESCE(@PayHerePaymentId, payhere_payment_id), updated_at = @UpdatedAt
            WHERE order_id = @OrderId;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Status", status.ToString());
        command.Parameters.AddWithValue("PayHerePaymentId", (object?)payHerePaymentId ?? DBNull.Value);
        command.Parameters.AddWithValue("UpdatedAt", updatedAt);
        command.Parameters.AddWithValue("OrderId", orderId);

        var rows = await command.ExecuteNonQueryAsync();
        return rows > 0;
    }
}
