using Booking.Service.Clients;
using Booking.Service.Db;
using Booking.Service.Models;
using Booking.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Booking.Service.Tests;

public class OrderServiceTests
{
    private readonly Mock<IOrderRepository> _orders = new();
    private readonly Mock<IInventoryClient> _inventory = new();
    private readonly Mock<IPayHereService> _payHere = new();
    private readonly Mock<ITicketService> _tickets = new();
    private readonly Mock<IKafkaProducer> _events = new();

    private OrderService Service() => new(
        _orders.Object, _inventory.Object, _payHere.Object, _tickets.Object,
        _events.Object, TimeProvider.System, NullLogger<OrderService>.Instance);

    [Fact]
    public async Task Existing_idempotency_key_returns_original_order_without_touching_inventory()
    {
        var original = NewOrder();
        _orders.Setup(x => x.FindByIdempotencyKeyAsync("customer", "key")).ReturnsAsync(original);

        var result = await Service().CreateOrderAsync("customer", "key", new CreateOrderRequest(original.HoldId));

        Assert.Equal(CreateOrderStatus.Duplicate, result.Status);
        Assert.Equal(original.Id, result.Order!.OrderId);
        _inventory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Missing_hold_does_not_create_an_order()
    {
        var holdId = Guid.CreateVersion7();
        var result = await Service().CreateOrderAsync("customer", "key", new CreateOrderRequest(holdId));

        Assert.Equal(CreateOrderStatus.HoldNotFound, result.Status);
        _orders.Verify(x => x.CreateAsync(It.IsAny<Order>()), Times.Never);
        _inventory.Verify(x => x.FreezeHoldAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Theory]
    [InlineData("other-customer", "Active", CreateOrderStatus.HoldUnauthorized)]
    [InlineData("customer", "Expired", CreateOrderStatus.HoldNotActive)]
    public async Task Invalid_hold_cannot_be_frozen_or_ordered(
        string holdCustomer, string holdStatus, CreateOrderStatus expected)
    {
        var hold = NewHold(holdCustomer, holdStatus);
        _inventory.Setup(x => x.GetHoldAsync(hold.HoldId)).ReturnsAsync(hold);

        var result = await Service().CreateOrderAsync("customer", "key", new CreateOrderRequest(hold.HoldId));

        Assert.Equal(expected, result.Status);
        _inventory.Verify(x => x.FreezeHoldAsync(It.IsAny<Guid>()), Times.Never);
        _orders.Verify(x => x.CreateAsync(It.IsAny<Order>()), Times.Never);
    }

    [Fact]
    public async Task Freeze_failure_does_not_create_an_order()
    {
        var hold = NewHold();
        _inventory.Setup(x => x.GetHoldAsync(hold.HoldId)).ReturnsAsync(hold);
        _inventory.Setup(x => x.FreezeHoldAsync(hold.HoldId)).ReturnsAsync(false);

        var result = await Service().CreateOrderAsync("customer", "key", new CreateOrderRequest(hold.HoldId));

        Assert.Equal(CreateOrderStatus.FreezeFailed, result.Status);
        _orders.Verify(x => x.CreateAsync(It.IsAny<Order>()), Times.Never);
    }

    [Fact]
    public async Task Active_hold_creates_order_with_items_and_seats()
    {
        var hold = NewHold();
        _inventory.Setup(x => x.GetHoldAsync(hold.HoldId)).ReturnsAsync(hold);
        _inventory.Setup(x => x.FreezeHoldAsync(hold.HoldId)).ReturnsAsync(true);
        Order? saved = null;
        _orders.Setup(x => x.CreateAsync(It.IsAny<Order>()))
            .Callback<Order>(order => saved = order)
            .ReturnsAsync((Order order) => new OrderCreationResult
            {
                Outcome = OrderCreationOutcome.Created,
                Order = order
            });

        var result = await Service().CreateOrderAsync("customer", "key", new CreateOrderRequest(hold.HoldId));

        Assert.Equal(CreateOrderStatus.Created, result.Status);
        Assert.NotNull(saved);
        Assert.Equal(250m, saved.TotalAmount);
        Assert.Equal(OrderStatus.PaymentPending, saved.Status);
        Assert.Equal("key", saved.IdempotencyKey);
        Assert.Single(saved.Seats);
        Assert.Single(result.Order!.Seats!);
    }

    [Fact]
    public async Task Concurrent_idempotency_conflict_returns_repository_order()
    {
        var hold = NewHold();
        var original = NewOrder();
        _inventory.Setup(x => x.GetHoldAsync(hold.HoldId)).ReturnsAsync(hold);
        _inventory.Setup(x => x.FreezeHoldAsync(hold.HoldId)).ReturnsAsync(true);
        _orders.Setup(x => x.CreateAsync(It.IsAny<Order>())).ReturnsAsync(new OrderCreationResult
        {
            Outcome = OrderCreationOutcome.Duplicate,
            Order = original
        });

        var result = await Service().CreateOrderAsync("customer", "key", new CreateOrderRequest(hold.HoldId));

        Assert.Equal(CreateOrderStatus.Duplicate, result.Status);
        Assert.Equal(original.Id, result.Order!.OrderId);
    }

    [Fact]
    public async Task Invalid_payment_signature_never_reads_or_changes_an_order()
    {
        var result = await Service().ProcessPaymentNotificationAsync(
            "merchant", Guid.CreateVersion7().ToString(), "250.00", "LKR", "2", "bad-signature");

        Assert.False(result);
        _orders.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Successful_payment_confirms_order_converts_hold_and_publishes_event()
    {
        var order = NewOrder();
        _payHere.Setup(x => x.VerifyNotificationSignature("merchant", order.Id.ToString(), "250.00", "LKR", "2", "signature"))
            .Returns(true);
        _orders.Setup(x => x.GetByIdAsync(order.Id)).ReturnsAsync(order);
        _orders.Setup(x => x.UpdateStatusAsync(order.Id, OrderStatus.Confirmed, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(true);
        _inventory.Setup(x => x.ConvertHoldAsync(order.HoldId)).ReturnsAsync(true);
        _tickets.Setup(x => x.IssueTicketsForOrderAsync(order)).ReturnsAsync([]);
        _events.Setup(x => x.PublishOrderConfirmedAsync(It.IsAny<object>())).Returns(Task.CompletedTask);

        var result = await Service().ProcessPaymentNotificationAsync(
            "merchant", order.Id.ToString(), "250.00", "LKR", "2", "signature");

        Assert.True(result);
        _inventory.Verify(x => x.ConvertHoldAsync(order.HoldId), Times.Once);
        _tickets.Verify(x => x.IssueTicketsForOrderAsync(order), Times.Once);
        _events.Verify(x => x.PublishOrderConfirmedAsync(It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task Failed_payment_marks_order_failed_and_releases_hold()
    {
        var order = NewOrder();
        _payHere.Setup(x => x.VerifyNotificationSignature("merchant", order.Id.ToString(), "250.00", "LKR", "0", "signature"))
            .Returns(true);
        _orders.Setup(x => x.GetByIdAsync(order.Id)).ReturnsAsync(order);
        _orders.Setup(x => x.UpdateStatusAsync(order.Id, OrderStatus.Failed, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(true);
        _inventory.Setup(x => x.ReleaseHoldAsync(order.HoldId)).ReturnsAsync(true);

        var result = await Service().ProcessPaymentNotificationAsync(
            "merchant", order.Id.ToString(), "250.00", "LKR", "0", "signature");

        Assert.True(result);
        _orders.Verify(x => x.UpdateStatusAsync(order.Id, OrderStatus.Failed, It.IsAny<DateTimeOffset>()), Times.Once);
        _inventory.Verify(x => x.ReleaseHoldAsync(order.HoldId), Times.Once);
        _tickets.VerifyNoOtherCalls();
        _events.VerifyNoOtherCalls();
    }

    private static InventoryHoldResponse NewHold(string customer = "customer", string status = "Active")
    {
        var categoryId = Guid.CreateVersion7();
        return new InventoryHoldResponse(Guid.CreateVersion7(), Guid.CreateVersion7(), customer, status,
            DateTimeOffset.UtcNow.AddMinutes(5), [new InventoryHoldItemResponse(categoryId, 2, 125m, "LKR")],
            [new InventoryHoldSeatResponse(Guid.CreateVersion7(), categoryId, "A", "1", 2)]);
    }

    private static Order NewOrder() => new()
    {
        Id = Guid.CreateVersion7(),
        HoldId = Guid.CreateVersion7(),
        ShowId = Guid.CreateVersion7(),
        CustomerSub = "customer",
        Status = OrderStatus.PaymentPending,
        TotalAmount = 250m,
        Currency = "LKR",
        CreatedAt = DateTimeOffset.UtcNow
    };
}
