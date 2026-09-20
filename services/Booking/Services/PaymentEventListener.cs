using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Booking.Service.Clients;
using Booking.Service.Db;
using Booking.Service.Models;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Booking.Service.Services;

public class PaymentEventListener : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaymentEventListener> _logger;
    private readonly string _bootstrapServers;

    public PaymentEventListener(
        IServiceScopeFactory scopeFactory,
        ILogger<PaymentEventListener> logger,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _bootstrapServers = configuration["Kafka:BootstrapServers"] ?? "localhost:9092";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _bootstrapServers,
            GroupId = "booking-service-payment-listeners",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            SocketTimeoutMs = 3000
        };

        await Task.Yield();

        try
        {
            using var consumer = new ConsumerBuilder<string, string>(config)
                .SetErrorHandler((_, e) =>
                {
                    if (e.IsBrokerError)
                    {
                        _logger.LogDebug("Kafka broker connection status: {Reason}", e.Reason);
                    }
                })
                .Build();
            consumer.Subscribe(new[] { "tickethive.payment.succeeded", "tickethive.payment.failed" });
            _logger.LogInformation("PaymentEventListener subscribed to payment Kafka topics");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var consumeResult = consumer.Consume(TimeSpan.FromSeconds(1));
                    if (consumeResult == null) continue;

                    using var scope = _scopeFactory.CreateScope();
                    var orderRepository = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
                    var inventoryClient = scope.ServiceProvider.GetRequiredService<IInventoryClient>();
                    var kafkaProducer = scope.ServiceProvider.GetRequiredService<IKafkaProducer>();
                    var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();

                    var now = timeProvider.GetUtcNow();

                    if (consumeResult.Topic == "tickethive.payment.succeeded")
                    {
                        using var doc = JsonDocument.Parse(consumeResult.Message.Value);
                        if (doc.RootElement.TryGetProperty("OrderId", out var orderIdProp) &&
                            Guid.TryParse(orderIdProp.GetString(), out var orderId))
                        {
                            var order = await orderRepository.GetByIdAsync(orderId);
                            if (order != null && order.Status != OrderStatus.Confirmed)
                            {
                                await orderRepository.UpdateStatusAsync(order.Id, OrderStatus.Confirmed, now);
                                await inventoryClient.ConvertHoldAsync(order.HoldId);

                                _logger.LogInformation("Order {OrderId} confirmed via Kafka event. Converted hold {HoldId}", order.Id, order.HoldId);

                                await kafkaProducer.PublishOrderConfirmedAsync(new
                                {
                                    OrderId = order.Id,
                                    HoldId = order.HoldId,
                                    CustomerSub = order.CustomerSub,
                                    ShowId = order.ShowId,
                                    TotalAmount = order.TotalAmount,
                                    Currency = order.Currency,
                                    ConfirmedAt = now
                                });
                            }
                        }
                    }
                    else if (consumeResult.Topic == "tickethive.payment.failed")
                    {
                        using var doc = JsonDocument.Parse(consumeResult.Message.Value);
                        if (doc.RootElement.TryGetProperty("OrderId", out var orderIdProp) &&
                            Guid.TryParse(orderIdProp.GetString(), out var orderId))
                        {
                            var order = await orderRepository.GetByIdAsync(orderId);
                            if (order != null && order.Status == OrderStatus.PaymentPending)
                            {
                                await orderRepository.UpdateStatusAsync(order.Id, OrderStatus.Failed, now);
                                await inventoryClient.ReleaseHoldAsync(order.HoldId);

                                _logger.LogInformation("Order {OrderId} failed via Kafka event. Released hold {HoldId}", order.Id, order.HoldId);
                            }
                        }
                    }
                }
                catch (ConsumeException ex)
                {
                    if (ex.Error.Code == ErrorCode.UnknownTopicOrPart)
                    {
                        _logger.LogDebug("Kafka topic not available yet: {Reason}", ex.Error.Reason);
                    }
                    else
                    {
                        _logger.LogWarning("Kafka consume exception in PaymentEventListener: {Reason}", ex.Error.Reason);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing payment Kafka event");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PaymentEventListener worker encountered fatal exception");
        }
    }
}
