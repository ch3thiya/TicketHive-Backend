using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Notification.Service.Db;
using Notification.Service.Models;

namespace Notification.Service.Services;

public class TicketIssuedEventListener : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TicketIssuedEventListener> _logger;
    private readonly string _bootstrapServers;

    public TicketIssuedEventListener(
        IServiceScopeFactory scopeFactory,
        ILogger<TicketIssuedEventListener> logger,
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
            GroupId = "notification-service-ticket-issued-group",
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

            consumer.Subscribe(new[] { "tickethive.tickets.issued", "tickethive.order.confirmed" });
            _logger.LogInformation("TicketIssuedEventListener subscribed to Kafka topics tickethive.tickets.issued and tickethive.order.confirmed");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var consumeResult = consumer.Consume(TimeSpan.FromSeconds(1));
                    if (consumeResult == null) continue;

                    _logger.LogInformation("[Kafka Listener Debug] Received message from topic '{Topic}', partition {Partition}, offset {Offset}: {Value}",
                        consumeResult.Topic, consumeResult.Partition.Value, consumeResult.Offset.Value, consumeResult.Message.Value);

                    using var scope = _scopeFactory.CreateScope();
                    var notificationRepo = scope.ServiceProvider.GetRequiredService<INotificationRepository>();
                    var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
                    var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();

                    using var doc = JsonDocument.Parse(consumeResult.Message.Value);
                    var root = doc.RootElement;

                    Guid orderId = Guid.Empty;
                    var orderIdProp = root.EnumerateObject()
                        .FirstOrDefault(p => string.Equals(p.Name, "OrderId", StringComparison.OrdinalIgnoreCase)).Value;

                    if (orderIdProp.ValueKind == JsonValueKind.String && Guid.TryParse(orderIdProp.GetString(), out var parsedOrderId))
                    {
                        orderId = parsedOrderId;
                    }

                    if (orderId == Guid.Empty)
                    {
                        _logger.LogWarning("[Kafka Listener Debug] Message missing valid OrderId property. Skipping.");
                        continue;
                    }

                    string rawEmail = root.EnumerateObject()
                        .FirstOrDefault(p => string.Equals(p.Name, "CustomerEmail", StringComparison.OrdinalIgnoreCase)).Value.GetString()
                        ?? root.EnumerateObject()
                        .FirstOrDefault(p => string.Equals(p.Name, "CustomerSub", StringComparison.OrdinalIgnoreCase)).Value.GetString()
                        ?? "customer@tickethive.lk";

                    string rawName = root.EnumerateObject()
                        .FirstOrDefault(p => string.Equals(p.Name, "CustomerName", StringComparison.OrdinalIgnoreCase)).Value.GetString()
                        ?? "Valued Customer";

                    var customerEmail = rawEmail.Contains('@') ? rawEmail : "customer@tickethive.lk";
                    var customerName = string.IsNullOrWhiteSpace(rawName) ? "Valued Customer" : rawName;

                    _logger.LogInformation("[Kafka Listener Debug] Extracted OrderId={OrderId}, Email='{Email}', Name='{Name}'", orderId, customerEmail, customerName);

                    var alreadySent = await notificationRepo.ExistsForOrderAndEmailAsync(orderId, customerEmail);
                    if (alreadySent)
                    {
                        _logger.LogInformation("[Kafka Listener Debug] Notification already sent for Order {OrderId} and Email {Email}. Skipping duplicate.", orderId, customerEmail);
                        continue;
                    }

                    var ticketCodes = new List<string>();
                    if (root.TryGetProperty("TicketCodes", out var codesArray) && codesArray.ValueKind == JsonValueKind.Array)
                    {
                        ticketCodes = codesArray.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                    }

                    decimal totalAmount = 0m;
                    if (root.TryGetProperty("TotalAmount", out var amountProp))
                    {
                        amountProp.TryGetDecimal(out totalAmount);
                    }
                    var currency = root.TryGetProperty("Currency", out var currProp) ? currProp.GetString() ?? "LKR" : "LKR";

                    var emailResult = await emailService.SendTicketConfirmationAsync(
                        customerEmail,
                        customerName,
                        orderId,
                        totalAmount,
                        currency,
                        ticketCodes);

                    var now = timeProvider.GetUtcNow();
                    var record = new NotificationRecord
                    {
                        Id = Guid.CreateVersion7(),
                        OrderId = orderId,
                        CustomerEmail = customerEmail,
                        Subject = $"Ticket Confirmation - Order #{orderId.ToString().Substring(0, 8).ToUpper()}",
                        Status = emailResult.Success ? "Sent" : "Failed",
                        ErrorMessage = emailResult.ErrorMessage,
                        SentAt = now
                    };

                    await notificationRepo.CreateAsync(record);
                    _logger.LogInformation("Notification record saved for Order {OrderId} with status {Status}", orderId, record.Status);
                }
                catch (ConsumeException ex)
                {
                    if (ex.Error.Code != ErrorCode.UnknownTopicOrPart)
                    {
                        _logger.LogWarning("Kafka consume exception in TicketIssuedEventListener: {Reason}", ex.Error.Reason);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing ticket issued Kafka notification event");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TicketIssuedEventListener worker encountered fatal exception");
        }
    }
}
