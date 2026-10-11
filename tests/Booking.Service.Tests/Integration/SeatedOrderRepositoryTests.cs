using Booking.Service.Db;
using Booking.Service.Models;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Booking.Service.Tests.Integration;

// Real PostgreSQL: seats survive the order, the ticket and a repeated issue.
[Collection("Postgres")]
public sealed class SeatedOrderRepositoryTests
{
    private readonly PostgresFixture _db;

    public SeatedOrderRepositoryTests(PostgresFixture db) => _db = db;

    private DbConnectionFactory Connections() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString })
        .Build());

    private static Order NewOrder(Guid category, params OrderSeat[] seats) => new()
    {
        Id = Guid.CreateVersion7(),
        HoldId = Guid.CreateVersion7(),
        CustomerSub = "customer",
        ShowId = Guid.CreateVersion7(),
        Status = OrderStatus.Confirmed,
        TotalAmount = 200,
        Currency = "LKR",
        IdempotencyKey = Guid.CreateVersion7().ToString(),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        CustomerEmail = "t@example.invalid",
        CustomerName = "T",
        Items = new List<OrderItem> { new() { CategoryId = category, Quantity = Math.Max(seats.Length, 1), UnitPrice = 100 } },
        Seats = seats.ToList()
    };

    private static OrderSeat Seat(Guid category, string row, int number) =>
        new() { SeatId = Guid.CreateVersion7(), CategoryId = category, Section = "Front", Row = row, Number = number };

    [Fact]
    public async Task OrderSeats_AreStoredAndReadBackInSeatOrder()
    {
        var category = Guid.NewGuid();
        var order = NewOrder(category, Seat(category, "B", 1), Seat(category, "A", 2));
        var repository = new OrderRepository(Connections());

        Assert.Equal(OrderCreationOutcome.Created, (await repository.CreateAsync(order)).Outcome);

        var stored = await repository.GetByIdAsync(order.Id);
        Assert.Equal(new[] { ("A", 2), ("B", 1) }, stored!.Seats.Select(s => (s.Row, s.Number)));
        Assert.Equal(order.Seats.Select(s => s.SeatId).Order(), stored.Seats.Select(s => s.SeatId).Order());
        Assert.Single(stored.Items);
    }

    [Fact]
    public async Task GeneralAdmissionOrder_HasNoSeats()
    {
        var order = NewOrder(Guid.NewGuid());
        var repository = new OrderRepository(Connections());
        await repository.CreateAsync(order);

        Assert.Empty((await repository.GetByIdAsync(order.Id))!.Seats);
    }

    [Fact]
    public async Task IdempotentOrderRetry_DoesNotDuplicateSeats()
    {
        var category = Guid.NewGuid();
        var order = NewOrder(category, Seat(category, "A", 1));
        var repository = new OrderRepository(Connections());
        await repository.CreateAsync(order);
        var retry = NewOrder(category, Seat(category, "A", 1));
        retry.CustomerSub = order.CustomerSub;
        retry.IdempotencyKey = order.IdempotencyKey;

        var result = await repository.CreateAsync(retry);

        Assert.Equal(OrderCreationOutcome.Duplicate, result.Outcome);
        Assert.Single(result.Order!.Seats);
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM order_seats WHERE order_id = @o", connection);
        count.Parameters.AddWithValue("o", order.Id);
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SeatedTickets_KeepTheirSeat_AndARepeatedIssueDoesNotDoubleBookIt()
    {
        var category = Guid.NewGuid();
        var order = NewOrder(category, Seat(category, "A", 1));
        await new OrderRepository(Connections()).CreateAsync(order);
        var tickets = new TicketRepository(Connections());
        Ticket Make(string code) => new()
        {
            Id = Guid.CreateVersion7(),
            OrderId = order.Id,
            CategoryId = category,
            ShowId = order.ShowId,
            CustomerSub = "customer",
            UniqueCode = code,
            Price = 100,
            IssuedAt = DateTimeOffset.UtcNow,
            SeatId = order.Seats[0].SeatId,
            SeatSection = "Front",
            SeatRow = "A",
            SeatNumber = 1
        };

        await tickets.CreateTicketsAsync(new[] { Make("TKT-ONE-" + Guid.NewGuid()) });
        await tickets.CreateTicketsAsync(new[] { Make("TKT-TWO-" + Guid.NewGuid()) });

        var stored = await tickets.GetByOrderIdAsync(order.Id);
        var ticket = Assert.Single(stored);
        Assert.Equal(order.Seats[0].SeatId, ticket.SeatId);
        Assert.Equal(("Front", "A", 1), (ticket.SeatSection, ticket.SeatRow, ticket.SeatNumber));
    }

    [Fact]
    public async Task GeneralAdmissionTickets_StillStoreWithoutASeat()
    {
        var category = Guid.NewGuid();
        var order = NewOrder(category);
        await new OrderRepository(Connections()).CreateAsync(order);
        var tickets = new TicketRepository(Connections());
        Ticket Make() => new()
        {
            Id = Guid.CreateVersion7(),
            OrderId = order.Id,
            CategoryId = category,
            ShowId = order.ShowId,
            CustomerSub = "customer",
            UniqueCode = "TKT-GA-" + Guid.NewGuid(),
            Price = 100,
            IssuedAt = DateTimeOffset.UtcNow
        };

        await tickets.CreateTicketsAsync(new[] { Make(), Make() });

        var stored = await tickets.GetByOrderIdAsync(order.Id);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, t => Assert.Null(t.SeatId));
    }
}
