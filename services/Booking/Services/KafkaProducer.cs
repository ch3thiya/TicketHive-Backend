using System;
using System.Text.Json;
using System.Threading.Tasks;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Booking.Service.Services;

public class KafkaProducer : IKafkaProducer
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<KafkaProducer> _logger;
    private readonly string _bootstrapServers;

    public KafkaProducer(IConfiguration configuration, ILogger<KafkaProducer> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _bootstrapServers = configuration["Kafka:BootstrapServers"] ?? "localhost:9092";
    }

    public async Task PublishOrderConfirmedAsync(object orderConfirmedEvent)
    {
        await ProduceTopicAsync("tickethive.order.confirmed", orderConfirmedEvent);
    }

    public async Task PublishTicketsIssuedAsync(object ticketsIssuedEvent)
    {
        await ProduceTopicAsync("tickethive.tickets.issued", ticketsIssuedEvent);
    }

    private async Task ProduceTopicAsync(string topic, object messagePayload)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = _bootstrapServers,
            Acks = Acks.All
        };

        try
        {
            using var producer = new ProducerBuilder<string, string>(config).Build();
            var json = JsonSerializer.Serialize(messagePayload);

            var result = await producer.ProduceAsync(topic, new Message<string, string>
            {
                Key = Guid.NewGuid().ToString(),
                Value = json
            });

            _logger.LogInformation("Published {Topic} to Kafka partition {Partition} at offset {Offset}", topic, result.Partition, result.Offset);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish {Topic} to Kafka", topic);
        }
    }
}
