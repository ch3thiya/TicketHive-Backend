using System;
using System.Threading.Tasks;
using Booking.Service.Models;

namespace Booking.Service.Db;

public enum OrderCreationOutcome
{
    Created,
    Duplicate
}

public class OrderCreationResult
{
    public required OrderCreationOutcome Outcome { get; init; }
    public Order? Order { get; init; }
}

public interface IOrderRepository
{
    Task<OrderCreationResult> CreateAsync(Order order);
    Task<Order?> GetByIdAsync(Guid orderId);
    Task<Order?> FindByIdempotencyKeyAsync(string customerSub, string idempotencyKey);
    Task<bool> UpdateStatusAsync(Guid orderId, OrderStatus newStatus, DateTimeOffset updatedAt);
}
