using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Booking.Service.Db;
using Booking.Service.Models;
using Microsoft.Extensions.Logging;

namespace Booking.Service.Services;

public class TicketService : ITicketService
{
    private readonly ITicketRepository _repository;
    private readonly ITicketCodeGenerator _codeGenerator;
    private readonly IKafkaProducer _kafkaProducer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TicketService> _logger;

    public TicketService(
        ITicketRepository repository,
        ITicketCodeGenerator codeGenerator,
        IKafkaProducer kafkaProducer,
        TimeProvider timeProvider,
        ILogger<TicketService> logger)
    {
        _repository = repository;
        _codeGenerator = codeGenerator;
        _kafkaProducer = kafkaProducer;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TicketResponse>> IssueTicketsForOrderAsync(Order order)
    {
        var existingTickets = await _repository.GetByOrderIdAsync(order.Id);
        if (existingTickets.Count > 0)
        {
            _logger.LogInformation("Order {OrderId} already has {Count} tickets issued. Returning existing tickets (Idempotent)", order.Id, existingTickets.Count);
            return existingTickets.Select(ToResponse).ToList();
        }

        var now = _timeProvider.GetUtcNow();
        var newTickets = new List<Ticket>();

        foreach (var item in order.Items)
        {
            for (int q = 0; q < item.Quantity; q++)
            {
                var ticket = new Ticket
                {
                    Id = Guid.CreateVersion7(),
                    OrderId = order.Id,
                    CategoryId = item.CategoryId,
                    ShowId = order.ShowId,
                    CustomerSub = order.CustomerSub,
                    UniqueCode = _codeGenerator.GenerateCode(),
                    Price = item.UnitPrice,
                    IssuedAt = now
                };
                newTickets.Add(ticket);
            }
        }

        await _repository.CreateTicketsAsync(newTickets);
        _logger.LogInformation("Issued {Count} tickets for Order {OrderId} (Customer {CustomerSub})", newTickets.Count, order.Id, order.CustomerSub);

        var customerEmail = !string.IsNullOrWhiteSpace(order.CustomerEmail)
            ? order.CustomerEmail
            : (order.CustomerSub.Contains('@') ? order.CustomerSub : "customer@tickethive.lk");
        var customerName = !string.IsNullOrWhiteSpace(order.CustomerName) ? order.CustomerName : "Valued Customer";

        _logger.LogInformation("[TicketService Debug] Publishing tickethive.tickets.issued Kafka event for Order {OrderId}. CustomerEmail: '{Email}', CustomerName: '{Name}'", order.Id, customerEmail, customerName);

        await _kafkaProducer.PublishTicketsIssuedAsync(new
        {
            OrderId = order.Id,
            ShowId = order.ShowId,
            CustomerSub = order.CustomerSub,
            CustomerEmail = customerEmail,
            CustomerName = customerName,
            TotalAmount = order.TotalAmount,
            Currency = order.Currency,
            TicketCount = newTickets.Count,
            TicketCodes = newTickets.Select(t => t.UniqueCode).ToList(),
            IssuedAt = now,
            TicketIds = newTickets.Select(t => t.Id).ToList()
        });

        return newTickets.Select(ToResponse).ToList();
    }

    public async Task<IReadOnlyList<TicketResponse>> GetCustomerTicketsAsync(string customerSub)
    {
        var tickets = await _repository.GetByCustomerSubAsync(customerSub);
        return tickets.Select(ToResponse).ToList();
    }

    public async Task<TicketResponse?> GetTicketByIdAsync(Guid ticketId, string customerSub)
    {
        var ticket = await _repository.GetByIdAsync(ticketId, customerSub);
        return ticket is null ? null : ToResponse(ticket);
    }

    public async Task<IReadOnlyList<TicketResponse>> GetOrderTicketsAsync(Guid orderId, string customerSub)
    {
        var tickets = await _repository.GetByOrderIdAsync(orderId);
        return tickets.Where(t => string.Equals(t.CustomerSub, customerSub, StringComparison.OrdinalIgnoreCase))
                      .Select(ToResponse)
                      .ToList();
    }

    public TicketResponse ToResponse(Ticket ticket) => new(
        ticket.Id,
        ticket.OrderId,
        ticket.ShowId,
        ticket.CategoryId,
        ticket.CustomerSub,
        ticket.UniqueCode,
        ticket.Price,
        ticket.IssuedAt,
        ticket.UsedAt,
        ticket.UsedBy
    );
}
