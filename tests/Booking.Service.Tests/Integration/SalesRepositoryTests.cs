using Booking.Service.Db;
using Booking.Service.Models;
using Booking.Service.Services;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Booking.Service.Tests.Integration;

// Real PostgreSQL: which orders and tickets count towards a show's sales (SCRUM-171 AC1, AC2, AC4-AC6).
[Collection("Postgres")]
public sealed class SalesRepositoryTests
{
    private readonly PostgresFixture _db;
    private readonly SalesTestData _data;

    public SalesRepositoryTests(PostgresFixture db)
    {
        _db = db;
        _data = new SalesTestData(db.ConnectionString);
    }

    private static readonly Guid Vip = Guid.CreateVersion7();
    private static readonly Guid General = Guid.CreateVersion7();

    private SalesService Service() =>
        new(new SalesRepository(_data.Connections()), new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero)));

    private async Task<ShowSales> SalesAsync(Guid show)
    {
        var result = await Service().GetShowSalesAsync(show);
        Assert.Equal(SalesOutcome.Ok, result.Outcome);
        return result.Sales!;
    }

    [Fact]
    public async Task ConfirmedOrders_AreSummedPerCategory_FromTheirItems()
    {
        var show = Guid.CreateVersion7();
        await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 2, 120m), (General, 1, 40m));
        await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (General, 3, 40m));

        var sales = await SalesAsync(show);

        Assert.Equal(6, sales.TicketsSold);
        Assert.Equal(400m, sales.Revenue);
        Assert.Equal("LKR", sales.Currency);
        Assert.Equal((2, 240m), (sales.Categories.Single(c => c.CategoryId == Vip).Sold, sales.Categories.Single(c => c.CategoryId == Vip).Revenue));
        Assert.Equal((4, 160m), (sales.Categories.Single(c => c.CategoryId == General).Sold, sales.Categories.Single(c => c.CategoryId == General).Revenue));
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero), sales.AsOf);
    }

    [Theory]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Failed)]
    [InlineData(OrderStatus.PaymentPending)]
    public async Task OrdersThatAreNotConfirmed_NeverCount(OrderStatus status)
    {
        var show = Guid.CreateVersion7();
        await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 1, 120m));
        await _data.AddOrderAsync(show, status, "LKR", (Vip, 5, 120m));

        var sales = await SalesAsync(show);

        Assert.Equal(1, sales.TicketsSold);
        Assert.Equal(120m, sales.Revenue);
    }

    [Fact]
    public async Task AnOrderCancelledAfterItWasConfirmed_DropsOutOfSalesAndEntryProgress()
    {
        var show = Guid.CreateVersion7();
        var kept = await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 1, 120m));
        var refunded = await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 2, 120m));
        await _data.IssueTicketsAsync(kept);
        var refundedTickets = await _data.IssueTicketsAsync(refunded);
        Assert.Equal(3, (await SalesAsync(show)).TicketsSold);

        await _data.SetOrderStatusAsync(refunded.Id, OrderStatus.Cancelled);
        foreach (var ticket in refundedTickets) await _data.VoidAsync(ticket);

        var sales = await SalesAsync(show);
        Assert.Equal(1, sales.TicketsSold);
        Assert.Equal(120m, sales.Revenue);
    }

    [Fact]
    public async Task EntryProgress_CountsOnlyValidatedTicketsThatAreNotVoided()
    {
        var show = Guid.CreateVersion7();
        var order = await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 2, 120m), (General, 2, 40m));
        var tickets = await _data.IssueTicketsAsync(order);
        await _data.UseAsync(tickets.First(t => t.CategoryId == Vip));
        await _data.UseAsync(tickets.First(t => t.CategoryId == General));
        var voidedAndUsed = tickets.Last(t => t.CategoryId == General);
        await _data.UseAsync(voidedAndUsed);
        await _data.VoidAsync(voidedAndUsed);

        var sales = await SalesAsync(show);

        Assert.Equal(4, sales.TicketsSold);
        Assert.Equal(2, sales.TicketsUsed);
        Assert.Equal(1, sales.Categories.Single(c => c.CategoryId == Vip).Used);
        Assert.Equal(1, sales.Categories.Single(c => c.CategoryId == General).Used);
    }

    [Fact]
    public async Task UsedTicketsOfACancelledOrder_AreNotCounted()
    {
        var show = Guid.CreateVersion7();
        var order = await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 1, 120m));
        var tickets = await _data.IssueTicketsAsync(order);
        await _data.UseAsync(tickets[0]);
        await _data.SetOrderStatusAsync(order.Id, OrderStatus.Cancelled);

        var sales = await SalesAsync(show);

        Assert.Equal(0, sales.TicketsUsed);
        Assert.Equal(0, sales.TicketsSold);
    }

    [Fact]
    public async Task OtherShowsOrders_AreNeverMixedIn()
    {
        var mine = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        await _data.AddOrderAsync(mine, OrderStatus.Confirmed, "LKR", (Vip, 1, 120m));
        var theirOrder = await _data.AddOrderAsync(other, OrderStatus.Confirmed, "LKR", (Vip, 9, 120m));
        var theirTickets = await _data.IssueTicketsAsync(theirOrder);
        await _data.UseAsync(theirTickets[0]);

        var sales = await SalesAsync(mine);

        Assert.Equal(1, sales.TicketsSold);
        Assert.Equal(120m, sales.Revenue);
        Assert.Equal(0, sales.TicketsUsed);
    }

    [Fact]
    public async Task ShowWithNoSales_ReturnsZerosAndNoCategories_NotAnError()
    {
        var sales = await SalesAsync(Guid.CreateVersion7());

        Assert.Equal((0, 0m, 0), (sales.TicketsSold, sales.Revenue, sales.TicketsUsed));
        Assert.Empty(sales.Categories);
        Assert.Equal("LKR", sales.Currency);
    }

    [Fact]
    public async Task ANewlyConfirmedOrder_IsReflectedOnTheNextRead()
    {
        var show = Guid.CreateVersion7();
        await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 1, 120m));
        Assert.Equal(1, (await SalesAsync(show)).TicketsSold);
        var pending = await _data.AddOrderAsync(show, OrderStatus.PaymentPending, "LKR", (General, 2, 40m));
        Assert.Equal(1, (await SalesAsync(show)).TicketsSold);

        await _data.SetOrderStatusAsync(pending.Id, OrderStatus.Confirmed);

        var sales = await SalesAsync(show);
        Assert.Equal(3, sales.TicketsSold);
        Assert.Equal(200m, sales.Revenue);
    }

    [Fact]
    public async Task SoldIsCountedFromTheOrderItems_EvenBeforeTicketsAreIssued()
    {
        var show = Guid.CreateVersion7();
        await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 2, 120m));

        var sales = await SalesAsync(show);

        Assert.Equal(2, sales.TicketsSold);
        Assert.Equal(0, sales.TicketsUsed);
    }

    [Fact]
    public async Task OrdersInDifferentCurrencies_AreNotCombined()
    {
        var show = Guid.CreateVersion7();
        await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 1, 120m));
        await _data.AddOrderAsync(show, OrderStatus.Confirmed, "USD", (Vip, 1, 5m));

        var result = await Service().GetShowSalesAsync(show);

        Assert.Equal(SalesOutcome.MixedCurrencies, result.Outcome);
        Assert.Null(result.Sales);
    }

    [Fact]
    public async Task SeatedAndGeneralAdmissionOrders_AreCountedTheSameWay()
    {
        var show = Guid.CreateVersion7();
        var seated = await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 2, 120m));
        seated.Seats.Add(new OrderSeat { OrderId = seated.Id, SeatId = Guid.CreateVersion7(), CategoryId = Vip, Section = "Front", Row = "A", Number = 1 });
        await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (General, 1, 40m));

        var sales = await SalesAsync(show);

        Assert.Equal(3, sales.TicketsSold);
        Assert.Equal(280m, sales.Revenue);
    }

    [Fact]
    public async Task ReadsDuringSalesAndScans_NeverShowMoreEnteredThanSold()
    {
        var show = Guid.CreateVersion7();
        using var stop = new CancellationTokenSource();

        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 40; i++)
            {
                var order = await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 1, 120m));
                var tickets = await _data.IssueTicketsAsync(order);
                await _data.UseAsync(tickets[0]);
            }

            stop.Cancel();
        });

        var violations = 0;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var sales = await SalesAsync(show);
                if (sales.TicketsUsed > sales.TicketsSold || sales.Categories.Any(c => c.Used > c.Sold)) Interlocked.Increment(ref violations);
            }
        })).ToArray();

        await Task.WhenAll(readers.Append(writer));

        Assert.Equal(0, violations);
        var final = await SalesAsync(show);
        Assert.Equal((40, 40, 4800m), (final.TicketsSold, final.TicketsUsed, final.Revenue));
    }

    [Fact]
    public async Task MoneyKeepsItsPrecision_AcrossManyOrders()
    {
        var show = Guid.CreateVersion7();
        for (var i = 0; i < 30; i++) await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 3, 33.33m));

        var sales = await SalesAsync(show);

        Assert.Equal(90, sales.TicketsSold);
        Assert.Equal(2999.70m, sales.Revenue);
    }
}
