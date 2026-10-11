using Booking.Service.Clients;
using Booking.Service.Db;
using Booking.Service.Models;
using Booking.Service.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Booking.Service.Tests;

public class SeatedTicketTests
{
    private readonly Mock<ITicketRepository> _tickets = new();
    private readonly Mock<IKafkaProducer> _producer = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly TicketService _service;
    private readonly Guid _vip = Guid.NewGuid();
    private readonly Guid _general = Guid.NewGuid();

    public SeatedTicketTests()
    {
        _service = new TicketService(_tickets.Object, new TicketCodeGenerator(), _producer.Object, _time, new Mock<ILogger<TicketService>>().Object);
    }

    private static OrderSeat Seat(Guid category, string section, string row, int number) =>
        new() { SeatId = Guid.NewGuid(), CategoryId = category, Section = section, Row = row, Number = number };

    private Order SeatedOrder(params OrderSeat[] seats) => new()
    {
        Id = Guid.NewGuid(),
        HoldId = Guid.NewGuid(),
        CustomerSub = "customer",
        ShowId = Guid.NewGuid(),
        Status = OrderStatus.Confirmed,
        TotalAmount = 300,
        Currency = "LKR",
        IdempotencyKey = "key",
        CreatedAt = _time.GetUtcNow(),
        UpdatedAt = _time.GetUtcNow(),
        Items = seats.GroupBy(s => s.CategoryId).Select(g => new OrderItem { CategoryId = g.Key, Quantity = g.Count(), UnitPrice = 100 }).ToList(),
        Seats = seats.ToList()
    };

    [Fact]
    public async Task SeatedOrder_IssuesOneTicketPerSeat_EachCarryingItsOwnSeat()
    {
        var order = SeatedOrder(Seat(_vip, "Front", "B", 2), Seat(_vip, "Front", "A", 1), Seat(_general, "Hall", "C", 4));
        _tickets.Setup(r => r.GetByOrderIdAsync(order.Id)).ReturnsAsync(new List<Ticket>());

        var tickets = await _service.IssueTicketsForOrderAsync(order);

        Assert.Equal(3, tickets.Count);
        Assert.Equal(3, tickets.Select(t => t.Seat).Distinct().Count());
        Assert.All(tickets, t => Assert.NotNull(t.Seat));
        var vip = tickets.Where(t => t.CategoryId == _vip).Select(t => t.Seat!).OrderBy(s => s.Row).ToList();
        Assert.Equal(new[] { new TicketSeat("Front", "A", 1), new TicketSeat("Front", "B", 2) }, vip);
        Assert.Equal(new TicketSeat("Hall", "C", 4), tickets.Single(t => t.CategoryId == _general).Seat);
        _tickets.Verify(r => r.CreateTicketsAsync(It.Is<IEnumerable<Ticket>>(t => t.Count(x => x.SeatId != null) == 3)), Times.Once);
    }

    [Fact]
    public async Task SeatedOrder_WithFewerSeatsThanTickets_IssuesNothing()
    {
        var order = SeatedOrder(Seat(_vip, "Front", "A", 1), Seat(_vip, "Front", "A", 2));
        order.Items[0].Quantity = 3;
        _tickets.Setup(r => r.GetByOrderIdAsync(order.Id)).ReturnsAsync(new List<Ticket>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.IssueTicketsForOrderAsync(order));

        _tickets.Verify(r => r.CreateTicketsAsync(It.IsAny<IEnumerable<Ticket>>()), Times.Never);
        _producer.Verify(p => p.PublishTicketsIssuedAsync(It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task GeneralAdmissionOrder_StillIssuesTicketsWithoutSeats()
    {
        var order = SeatedOrder();
        order.Items = new List<OrderItem> { new() { CategoryId = _general, Quantity = 2, UnitPrice = 100 } };
        _tickets.Setup(r => r.GetByOrderIdAsync(order.Id)).ReturnsAsync(new List<Ticket>());

        var tickets = await _service.IssueTicketsForOrderAsync(order);

        Assert.Equal(2, tickets.Count);
        Assert.All(tickets, t => Assert.Null(t.Seat));
    }

    [Fact]
    public async Task RepeatedIssue_ReturnsTheExistingSeatedTicketsWithoutIssuingAgain()
    {
        var order = SeatedOrder(Seat(_vip, "Front", "A", 1));
        var existing = new Ticket
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            CategoryId = _vip,
            ShowId = order.ShowId,
            CustomerSub = "customer",
            UniqueCode = "TKT-X",
            Price = 100,
            IssuedAt = _time.GetUtcNow(),
            SeatId = order.Seats[0].SeatId,
            SeatSection = "Front",
            SeatRow = "A",
            SeatNumber = 1
        };
        _tickets.Setup(r => r.GetByOrderIdAsync(order.Id)).ReturnsAsync(new List<Ticket> { existing });

        var tickets = await _service.IssueTicketsForOrderAsync(order);

        Assert.Equal(new TicketSeat("Front", "A", 1), Assert.Single(tickets).Seat);
        _tickets.Verify(r => r.CreateTicketsAsync(It.IsAny<IEnumerable<Ticket>>()), Times.Never);
    }
}
