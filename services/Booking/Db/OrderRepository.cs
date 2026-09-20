using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Booking.Service.Models;
using Npgsql;

namespace Booking.Service.Db;

public class OrderRepository : IOrderRepository
{
    private readonly DbConnectionFactory _connectionFactory;

    public OrderRepository(DbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<OrderCreationResult> CreateAsync(Order order)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        const string orderInsertSql = @"
            INSERT INTO orders (id, hold_id, customer_sub, show_id, status, total_amount, currency, idempotency_key, created_at, updated_at)
            VALUES (@Id, @HoldId, @CustomerSub, @ShowId, @Status, @TotalAmount, @Currency, @IdempotencyKey, @CreatedAt, @UpdatedAt);
        ";

        const string itemInsertSql = @"
            INSERT INTO order_items (order_id, category_id, quantity, unit_price)
            VALUES (@OrderId, @CategoryId, @Quantity, @UnitPrice);
        ";

        try
        {
            await using (var command = new NpgsqlCommand(orderInsertSql, connection, transaction))
            {
                command.Parameters.AddWithValue("Id", order.Id);
                command.Parameters.AddWithValue("HoldId", order.HoldId);
                command.Parameters.AddWithValue("CustomerSub", order.CustomerSub);
                command.Parameters.AddWithValue("ShowId", order.ShowId);
                command.Parameters.AddWithValue("Status", order.Status.ToString());
                command.Parameters.AddWithValue("TotalAmount", order.TotalAmount);
                command.Parameters.AddWithValue("Currency", order.Currency);
                command.Parameters.AddWithValue("IdempotencyKey", order.IdempotencyKey);
                command.Parameters.AddWithValue("CreatedAt", order.CreatedAt);
                command.Parameters.AddWithValue("UpdatedAt", order.UpdatedAt);
                await command.ExecuteNonQueryAsync();
            }

            foreach (var item in order.Items)
            {
                await using var command = new NpgsqlCommand(itemInsertSql, connection, transaction);
                command.Parameters.AddWithValue("OrderId", order.Id);
                command.Parameters.AddWithValue("CategoryId", item.CategoryId);
                command.Parameters.AddWithValue("Quantity", item.Quantity);
                command.Parameters.AddWithValue("UnitPrice", item.UnitPrice);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            return new OrderCreationResult { Outcome = OrderCreationOutcome.Created, Order = order };
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync();
            var existing = await FindByIdempotencyKeyAsync(order.CustomerSub, order.IdempotencyKey);
            return new OrderCreationResult { Outcome = OrderCreationOutcome.Duplicate, Order = existing };
        }
    }

    public async Task<Order?> GetByIdAsync(Guid orderId)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT o.id, o.hold_id, o.customer_sub, o.show_id, o.status, o.total_amount, o.currency, o.idempotency_key, o.created_at, o.updated_at,
                   oi.category_id, oi.quantity, oi.unit_price
            FROM orders o
            LEFT JOIN order_items oi ON oi.order_id = o.id
            WHERE o.id = @OrderId;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("OrderId", orderId);

        await using var reader = await command.ExecuteReaderAsync();
        Order? order = null;

        while (await reader.ReadAsync())
        {
            order ??= new Order
            {
                Id = reader.GetGuid(0),
                HoldId = reader.GetGuid(1),
                CustomerSub = reader.GetString(2),
                ShowId = reader.GetGuid(3),
                Status = Enum.Parse<OrderStatus>(reader.GetString(4)),
                TotalAmount = reader.GetDecimal(5),
                Currency = reader.GetString(6).Trim(),
                IdempotencyKey = reader.GetString(7),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(8),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(9)
            };

            if (!reader.IsDBNull(10))
            {
                order.Items.Add(new OrderItem
                {
                    OrderId = order.Id,
                    CategoryId = reader.GetGuid(10),
                    Quantity = reader.GetInt32(11),
                    UnitPrice = reader.GetDecimal(12)
                });
            }
        }

        return order;
    }

    public async Task<Order?> FindByIdempotencyKeyAsync(string customerSub, string idempotencyKey)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = "SELECT id FROM orders WHERE customer_sub = @CustomerSub AND idempotency_key = @IdempotencyKey;";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("CustomerSub", customerSub);
        command.Parameters.AddWithValue("IdempotencyKey", idempotencyKey);

        var orderId = await command.ExecuteScalarAsync();
        return orderId is null ? null : await GetByIdAsync((Guid)orderId);
    }

    public async Task<bool> UpdateStatusAsync(Guid orderId, OrderStatus newStatus, DateTimeOffset updatedAt)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            UPDATE orders
            SET status = @Status, updated_at = @UpdatedAt
            WHERE id = @OrderId;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Status", newStatus.ToString());
        command.Parameters.AddWithValue("UpdatedAt", updatedAt);
        command.Parameters.AddWithValue("OrderId", orderId);

        var rows = await command.ExecuteNonQueryAsync();
        return rows > 0;
    }
}
