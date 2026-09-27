using System;
using System.Threading.Tasks;
using Payment.Service.Models;

namespace Payment.Service.Db;

public interface IPaymentRepository
{
    Task<PaymentTransaction> CreateAsync(PaymentTransaction transaction);
    Task<PaymentTransaction?> GetByOrderIdAsync(Guid orderId);
    Task<bool> UpdateStatusAsync(Guid orderId, PaymentStatus status, string? payHerePaymentId, DateTimeOffset updatedAt);
}
