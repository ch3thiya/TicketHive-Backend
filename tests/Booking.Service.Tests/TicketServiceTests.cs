using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Booking.Service.Db;
using Booking.Service.Models;
using Booking.Service.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Booking.Service.Tests;

public class TicketServiceTests
{
    private readonly Mock<ITicketRepository> _mockRepo;
    private readonly ITicketCodeGenerator _codeGenerator;
    private readonly Mock<IKafkaProducer> _mockProducer;
    private readonly FakeTimeProvider _timeProvider;
    private readonly TicketService _service;

    public TicketServiceTests()
    {
        _mockRepo = new Mock<ITicketRepository>();
        _codeGenerator = new TicketCodeGenerator();
        _mockProducer = new Mock<IKafkaProducer>();
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 23, 14, 0, 0, TimeSpan.Zero));
        _service = new TicketService(_mockRepo.Object, _codeGenerator, _mockProducer.Object, _timeProvider, new Mock<ILogger<TicketService>>().Object);
    }

    [Fact]
    public async Task IssueTicketsForOrderAsync_NewConfirmedOrder_IssuesOneTicketPerItemQuantityWithUniqueCodes()
    {
        // Arrange — Order with 2 tickets in category A and 1 ticket in category B
        var orderId = Guid.NewGuid();
        var catA = Guid.NewGuid();
        var catB = Guid.NewGuid();
        var order = new Order
        {
            Id = orderId,
            HoldId = Guid.NewGuid(),
            CustomerSub = "customer-sub-1",
            ShowId = Guid.NewGuid(),
            Status = OrderStatus.Confirmed,
            TotalAmount = 300,
            Currency = "LKR",
            IdempotencyKey = "idemp-key-1",
            CreatedAt = _timeProvider.GetUtcNow(),
            UpdatedAt = _timeProvider.GetUtcNow(),
            Items = new List<OrderItem>
            {
                new() { CategoryId = catA, Quantity = 2, UnitPrice = 100 },
                new() { CategoryId = catB, Quantity = 1, UnitPrice = 100 }
            }
        };

        _mockRepo.Setup(r => r.GetByOrderIdAsync(orderId)).ReturnsAsync(new List<Ticket>());

        // Act
        var result = await _service.IssueTicketsForOrderAsync(order);

        // Assert — SCRUM-16 AC1: 3 tickets issued (2 for catA, 1 for catB)
        Assert.Equal(3, result.Count);
        Assert.Equal(2, result.Count(t => t.CategoryId == catA));
        Assert.Equal(1, result.Count(t => t.CategoryId == catB));

        // SCRUM-16 AC2: Every ticket has a unique code starting with TKT-
        var uniqueCodes = result.Select(t => t.UniqueCode).Distinct().ToList();
        Assert.Equal(3, uniqueCodes.Count);
        Assert.All(uniqueCodes, code => Assert.StartsWith("TKT-", code));

        _mockRepo.Verify(r => r.CreateTicketsAsync(It.Is<IEnumerable<Ticket>>(t => t.Count() == 3)), Times.Once);
        _mockProducer.Verify(p => p.PublishTicketsIssuedAsync(It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task IssueTicketsForOrderAsync_AlreadyIssued_ReturnsExistingTicketsWithoutCreatingDuplicates()
    {
        // Arrange — Order already has issued tickets
        var orderId = Guid.NewGuid();
        var order = new Order
        {
            Id = orderId,
            HoldId = Guid.NewGuid(),
            CustomerSub = "customer-sub-1",
            ShowId = Guid.NewGuid(),
            Status = OrderStatus.Confirmed,
            TotalAmount = 100,
            Currency = "LKR",
            IdempotencyKey = "idemp-key-1",
            CreatedAt = _timeProvider.GetUtcNow(),
            UpdatedAt = _timeProvider.GetUtcNow(),
            Items = new List<OrderItem> { new() { CategoryId = Guid.NewGuid(), Quantity = 1, UnitPrice = 100 } }
        };

        var existingTicket = new Ticket
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            CategoryId = order.Items[0].CategoryId,
            ShowId = order.ShowId,
            CustomerSub = order.CustomerSub,
            UniqueCode = "TKT-TEST-CODE",
            Price = 100,
            IssuedAt = _timeProvider.GetUtcNow()
        };

        _mockRepo.Setup(r => r.GetByOrderIdAsync(orderId)).ReturnsAsync(new List<Ticket> { existingTicket });

        // Act
        var result = await _service.IssueTicketsForOrderAsync(order);

        // Assert — SCRUM-16 AC4: Returns existing ticket, no duplicates created
        Assert.Single(result);
        Assert.Equal("TKT-TEST-CODE", result[0].UniqueCode);
        _mockRepo.Verify(r => r.CreateTicketsAsync(It.IsAny<IEnumerable<Ticket>>()), Times.Never);
    }

    [Fact]
    public void TicketCodeGenerator_GeneratesUniqueUnpredictableCodes()
    {
        var generator = new TicketCodeGenerator();
        var codes = Enumerable.Range(0, 100).Select(_ => generator.GenerateCode()).ToList();

        Assert.Equal(100, codes.Distinct().Count());
        Assert.All(codes, code => Assert.Matches(@"^TKT-[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{4}-[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{4}-[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{4}$", code));
    }
}
