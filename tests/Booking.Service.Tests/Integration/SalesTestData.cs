using Booking.Service.Db;
using Booking.Service.Models;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Booking.Service.Tests.Integration;

// Builds orders and tickets directly in PostgreSQL for the sales tests.
internal sealed class SalesTestData(string connectionString)
{
    public DbConnectionFactory Connections() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = connectionString })
        .Build());

    public async Task<Order> AddOrderAsync(
        Guid showId, OrderStatus status, string currency = "LKR", params (Guid Category, int Quantity, decimal Price)[] items)
    {
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            HoldId = Guid.CreateVersion7(),
            CustomerSub = "customer",
            ShowId = showId,
            Status = status,
            TotalAmount = items.Sum(i => i.Quantity * i.Price),
            Currency = currency,
            IdempotencyKey = Guid.CreateVersion7().ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CustomerEmail = "t@example.invalid",
            CustomerName = "T",
            Items = items.Select(i => new OrderItem { CategoryId = i.Category, Quantity = i.Quantity, UnitPrice = i.Price }).ToList()
        };
        await new OrderRepository(Connections()).CreateAsync(order);
        return order;
    }

    // Issues one ticket per sold item, as the real flow does after payment.
    public async Task<List<Ticket>> IssueTicketsAsync(Order order)
    {
        var tickets = order.Items.SelectMany(item => Enumerable.Range(0, item.Quantity).Select(_ => new Ticket
        {
            Id = Guid.CreateVersion7(),
            OrderId = order.Id,
            CategoryId = item.CategoryId,
            ShowId = order.ShowId,
            CustomerSub = order.CustomerSub,
            UniqueCode = "TKT-" + Guid.NewGuid().ToString("N")[..12],
            Price = item.UnitPrice,
            IssuedAt = DateTimeOffset.UtcNow
        })).ToList();
        await new TicketRepository(Connections()).CreateTicketsAsync(tickets);
        return tickets;
    }

    public Task SetOrderStatusAsync(Guid orderId, OrderStatus status) =>
        ExecuteAsync("UPDATE orders SET status = @s WHERE id = @o", ("s", status.ToString()), ("o", orderId));

    public Task UseAsync(Ticket ticket) =>
        ExecuteAsync("UPDATE tickets SET used_at = now(), used_by = 'door' WHERE id = @t", ("t", ticket.Id));

    public Task VoidAsync(Ticket ticket) =>
        ExecuteAsync("UPDATE tickets SET voided_at = now() WHERE id = @t", ("t", ticket.Id));

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
