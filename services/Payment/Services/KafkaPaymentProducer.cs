using System;
using System.Text.Json;
using System.Threading.Tasks;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Payment.Service.Services;

public class KafkaPaymentProducer : IKafkaPaymentProducer
{
    private readonly ILogger<KafkaPaymentProducer> _logger;
    private readonly string _bootstrapServers;

    public KafkaPaymentProducer(IConfiguration configuration, ILogger<KafkaPaymentProducer> logger)
    {
        _logger = logger;
        _bootstrapServers = configuration["Kafka:BootstrapServers"] ?? "localhost:9092";
    }

    public Task PublishPaymentSucceededAsync(Guid orderId, string? paymentId, decimal amount, string currency, DateTimeOffset timestamp)
    {
        return PublishEventAsync("tickethive.payment.succeeded", new
        {
            OrderId = orderId,
            PaymentId = paymentId,
            Amount = amount,
            Currency = currency,
            Timestamp = timestamp
        });
    }

    public Task PublishPaymentFailedAsync(Guid orderId, string? paymentId, string reason, DateTimeOffset timestamp)
    {
        return PublishEventAsync("tickethive.payment.failed", new
        {
            OrderId = orderId,
            PaymentId = paymentId,
            Reason = reason,
            Timestamp = timestamp
        });
    }

    private async Task PublishEventAsync(string topic, object data)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = _bootstrapServers,
            Acks = Acks.All
        };

        try
        {
            using var producer = new ProducerBuilder<string, string>(config).Build();
            var json = JsonSerializer.Serialize(data);
            var result = await producer.ProduceAsync(topic, new Message<string, string>
            {
                Key = Guid.NewGuid().ToString(),
                Value = json
            });

            _logger.LogInformation("Published {Topic} event to Kafka partition {Partition} offset {Offset}", topic, result.Partition, result.Offset);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish {Topic} event to Kafka", topic);
        }
    }
}
