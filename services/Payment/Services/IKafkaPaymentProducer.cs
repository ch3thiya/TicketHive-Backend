using System;
using System.Threading.Tasks;

namespace Payment.Service.Services;

public interface IKafkaPaymentProducer
{
    Task PublishPaymentSucceededAsync(Guid orderId, string? paymentId, decimal amount, string currency, DateTimeOffset timestamp);
    Task PublishPaymentFailedAsync(Guid orderId, string? paymentId, string reason, DateTimeOffset timestamp);
}
