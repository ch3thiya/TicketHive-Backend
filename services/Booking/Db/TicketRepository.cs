using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Booking.Service.Models;
using Npgsql;

namespace Booking.Service.Db;

public class TicketRepository : ITicketRepository
{
    private readonly DbConnectionFactory _connectionFactory;

    public TicketRepository(DbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task CreateTicketsAsync(IEnumerable<Ticket> tickets)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        const string sql = @"
            INSERT INTO tickets (id, order_id, category_id, show_id, customer_sub, unique_code, price, issued_at, used_at, used_by)
            VALUES (@Id, @OrderId, @CategoryId, @ShowId, @CustomerSub, @UniqueCode, @Price, @IssuedAt, @UsedAt, @UsedBy)
            ON CONFLICT (unique_code) DO NOTHING;
        ";

        foreach (var ticket in tickets)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("Id", ticket.Id);
            command.Parameters.AddWithValue("OrderId", ticket.OrderId);
            command.Parameters.AddWithValue("CategoryId", ticket.CategoryId);
            command.Parameters.AddWithValue("ShowId", ticket.ShowId);
            command.Parameters.AddWithValue("CustomerSub", ticket.CustomerSub);
            command.Parameters.AddWithValue("UniqueCode", ticket.UniqueCode);
            command.Parameters.AddWithValue("Price", ticket.Price);
            command.Parameters.AddWithValue("IssuedAt", ticket.IssuedAt);
            command.Parameters.AddWithValue("UsedAt", (object?)ticket.UsedAt ?? DBNull.Value);
            command.Parameters.AddWithValue("UsedBy", (object?)ticket.UsedBy ?? DBNull.Value);

            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<Ticket>> GetByOrderIdAsync(Guid orderId)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT id, order_id, category_id, show_id, customer_sub, unique_code, price, issued_at, used_at, used_by, voided_at
            FROM tickets
            WHERE order_id = @OrderId
            ORDER BY issued_at ASC;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("OrderId", orderId);

        await using var reader = await command.ExecuteReaderAsync();
        var list = new List<Ticket>();
        while (await reader.ReadAsync())
        {
            list.Add(ReadTicket(reader));
        }

        return list;
    }

    public async Task<IReadOnlyList<Ticket>> GetByCustomerSubAsync(string customerSub)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT id, order_id, category_id, show_id, customer_sub, unique_code, price, issued_at, used_at, used_by, voided_at
            FROM tickets
            WHERE customer_sub = @CustomerSub
            ORDER BY issued_at DESC;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("CustomerSub", customerSub);

        await using var reader = await command.ExecuteReaderAsync();
        var list = new List<Ticket>();
        while (await reader.ReadAsync())
        {
            list.Add(ReadTicket(reader));
        }

        return list;
    }

    public async Task<Ticket?> GetByIdAsync(Guid ticketId, string customerSub)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT id, order_id, category_id, show_id, customer_sub, unique_code, price, issued_at, used_at, used_by, voided_at
            FROM tickets
            WHERE id = @TicketId AND customer_sub = @CustomerSub;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("TicketId", ticketId);
        command.Parameters.AddWithValue("CustomerSub", customerSub);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return ReadTicket(reader);
        }

        return null;
    }

    public async Task<Ticket?> GetByCodeAsync(string uniqueCode)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT id, order_id, category_id, show_id, customer_sub, unique_code, price, issued_at, used_at, used_by, voided_at
            FROM tickets
            WHERE unique_code = @UniqueCode;
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("UniqueCode", uniqueCode);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return ReadTicket(reader);
        }

        return null;
    }

    public async Task<bool> ValidateAndUseTicketAsync(string uniqueCode, string organizerSub, DateTimeOffset usedAt)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT validate_ticket(@UniqueCode, @UsedBy, @UsedAt);
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("UniqueCode", uniqueCode);
        command.Parameters.AddWithValue("UsedBy", organizerSub);
        command.Parameters.AddWithValue("UsedAt", usedAt);

        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static Ticket ReadTicket(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        OrderId = reader.GetGuid(1),
        CategoryId = reader.GetGuid(2),
        ShowId = reader.GetGuid(3),
        CustomerSub = reader.GetString(4),
        UniqueCode = reader.GetString(5),
        Price = reader.GetDecimal(6),
        IssuedAt = reader.GetFieldValue<DateTimeOffset>(7),
        UsedAt = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
        UsedBy = reader.IsDBNull(9) ? null : reader.GetString(9),
        VoidedAt = reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10)
    };
}
