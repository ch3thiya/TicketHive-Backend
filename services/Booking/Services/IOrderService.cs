using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Booking.Service.Models;

namespace Booking.Service.Services;

public record CreateOrderRequest(Guid HoldId);

public record OrderItemResponse(Guid CategoryId, int Quantity, decimal UnitPrice);

public record OrderResponse(Guid OrderId, Guid HoldId, string CustomerSub, Guid ShowId, string Status, decimal TotalAmount, string Currency, DateTimeOffset CreatedAt, List<OrderItemResponse> Items);

public enum CreateOrderStatus
{
    Created,
    Duplicate,
    HoldNotFound,
    HoldUnauthorized,
    HoldNotActive,
    FreezeFailed
}

public class CreateOrderResult
{
    public required CreateOrderStatus Status { get; init; }
    public OrderResponse? Order { get; init; }
}

public interface IOrderService
{
    Task<CreateOrderResult> CreateOrderAsync(string customerSub, string idempotencyKey, CreateOrderRequest request);
    Task<Order?> GetOrderAsync(Guid orderId);
    Task<bool> ProcessPaymentNotificationAsync(string merchantId, string orderId, string payhereAmount, string payhereCurrency, string statusCode, string md5sig);
    OrderResponse ToResponse(Order order);
}
