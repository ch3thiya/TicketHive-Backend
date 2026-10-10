using System;
using System.Linq;
using System.Threading.Tasks;
using Booking.Service.Clients;
using Booking.Service.Db;
using Booking.Service.Models;
using Microsoft.Extensions.Logging;

namespace Booking.Service.Services;

public class OrderService : IOrderService
{
    private readonly IOrderRepository _repository;
    private readonly IInventoryClient _inventoryClient;
    private readonly IPayHereService _payHereService;
    private readonly ITicketService _ticketService;
    private readonly IKafkaProducer _kafkaProducer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OrderService> _logger;

    public OrderService(
        IOrderRepository repository,
        IInventoryClient inventoryClient,
        IPayHereService payHereService,
        ITicketService ticketService,
        IKafkaProducer kafkaProducer,
        TimeProvider timeProvider,
        ILogger<OrderService> logger)
    {
        _repository = repository;
        _inventoryClient = inventoryClient;
        _payHereService = payHereService;
        _ticketService = ticketService;
        _kafkaProducer = kafkaProducer;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<CreateOrderResult> CreateOrderAsync(string customerSub, string idempotencyKey, CreateOrderRequest request)
    {
        var existingOrder = await _repository.FindByIdempotencyKeyAsync(customerSub, idempotencyKey);
        if (existingOrder is not null)
        {
            return new CreateOrderResult { Status = CreateOrderStatus.Duplicate, Order = ToResponse(existingOrder) };
        }

        var hold = await _inventoryClient.GetHoldAsync(request.HoldId);
        if (hold is null)
        {
            return new CreateOrderResult { Status = CreateOrderStatus.HoldNotFound };
        }

        if (!string.Equals(hold.CustomerSub, customerSub, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Customer {CustomerSub} attempted to create order for hold {HoldId} belonging to {HoldCustomerSub}", customerSub, request.HoldId, hold.CustomerSub);
            return new CreateOrderResult { Status = CreateOrderStatus.HoldUnauthorized };
        }

        if (!string.Equals(hold.Status, "Active", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Hold {HoldId} status is '{Status}', expected 'Active'", request.HoldId, hold.Status);
            return new CreateOrderResult { Status = CreateOrderStatus.HoldNotActive };
        }

        var frozen = await _inventoryClient.FreezeHoldAsync(request.HoldId);
        if (!frozen)
        {
            _logger.LogWarning("Failed to freeze hold {HoldId} in Inventory Service", request.HoldId);
            return new CreateOrderResult { Status = CreateOrderStatus.FreezeFailed };
        }

        var now = _timeProvider.GetUtcNow();
        var totalAmount = hold.Items.Sum(i => i.Quantity * i.UnitPrice);
        var currency = hold.Items.FirstOrDefault()?.Currency ?? "LKR";

        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            HoldId = hold.HoldId,
            CustomerSub = customerSub,
            ShowId = hold.ShowId,
            Status = OrderStatus.PaymentPending,
            TotalAmount = totalAmount,
            Currency = currency,
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
            UpdatedAt = now,
            Items = hold.Items.Select(i => new OrderItem
            {
                CategoryId = i.CategoryId,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice
            }).ToList()
        };

        var result = await _repository.CreateAsync(order);

        if (result.Outcome == OrderCreationOutcome.Duplicate)
        {
            return new CreateOrderResult { Status = CreateOrderStatus.Duplicate, Order = ToResponse(result.Order!) };
        }

        _logger.LogInformation("Order {OrderId} created for customer {CustomerSub} (Hold {HoldId})", order.Id, customerSub, hold.HoldId);
        return new CreateOrderResult { Status = CreateOrderStatus.Created, Order = ToResponse(order) };
    }

    public Task<Order?> GetOrderAsync(Guid orderId) => _repository.GetByIdAsync(orderId);

    public async Task<bool> ProcessPaymentNotificationAsync(string merchantId, string orderId, string payhereAmount, string payhereCurrency, string statusCode, string md5sig)
    {
        if (!_payHereService.VerifyNotificationSignature(merchantId, orderId, payhereAmount, payhereCurrency, statusCode, md5sig))
        {
            _logger.LogWarning("Invalid PayHere signature for notification on order {OrderId}", orderId);
            return false;
        }

        if (!Guid.TryParse(orderId, out var orderGuid))
        {
            _logger.LogWarning("Invalid OrderId format in PayHere notification: {OrderId}", orderId);
            return false;
        }

        var order = await _repository.GetByIdAsync(orderGuid);
        if (order is null)
        {
            _logger.LogWarning("Order {OrderId} not found for PayHere notification", orderId);
            return false;
        }

        if (order.Status == OrderStatus.Confirmed)
        {
            _logger.LogInformation("Order {OrderId} is already confirmed, ignoring duplicate payment notification", orderId);
            return true;
        }

        if (order.Status != OrderStatus.PaymentPending)
        {
            if (statusCode == "2") await _repository.UpdateStatusAsync(order.Id, OrderStatus.Confirmed, _timeProvider.GetUtcNow());
            return true;
        }

        var now = _timeProvider.GetUtcNow();

        if (statusCode == "2") // 2 = Success in PayHere
        {
            if (!await _repository.UpdateStatusAsync(order.Id, OrderStatus.Confirmed, now)) return true;
            await _inventoryClient.ConvertHoldAsync(order.HoldId);

            _logger.LogInformation("Order {OrderId} marked Confirmed. Converting hold {HoldId}", order.Id, order.HoldId);

            var issuedTickets = await _ticketService.IssueTicketsForOrderAsync(order);

            await _kafkaProducer.PublishOrderConfirmedAsync(new
            {
                OrderId = order.Id,
                HoldId = order.HoldId,
                CustomerSub = order.CustomerSub,
                ShowId = order.ShowId,
                TotalAmount = order.TotalAmount,
                Currency = order.Currency,
                ConfirmedAt = now,
                TicketCount = issuedTickets.Count,
                Items = order.Items.Select(i => new { i.CategoryId, i.Quantity, i.UnitPrice })
            });

            return true;
        }
        else
        {
            await _repository.UpdateStatusAsync(order.Id, OrderStatus.Failed, now);
            await _inventoryClient.ReleaseHoldAsync(order.HoldId);

            _logger.LogInformation("Order {OrderId} payment failed/cancelled (status_code {StatusCode}). Releasing hold {HoldId}", order.Id, statusCode, order.HoldId);
            return true;
        }
    }

    public OrderResponse ToResponse(Order order) => new(
        order.Id,
        order.HoldId,
        order.CustomerSub,
        order.ShowId,
        order.Status.ToString(),
        order.TotalAmount,
        order.Currency,
        order.CreatedAt,
        order.Items.Select(i => new OrderItemResponse(i.CategoryId, i.Quantity, i.UnitPrice)).ToList()
    );
}
