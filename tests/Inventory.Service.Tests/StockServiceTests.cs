using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;
using Inventory.Service.Db;
using Inventory.Service.Models;
using Inventory.Service.Services;

namespace Inventory.Service.Tests;

public class StockServiceTests
{
    private readonly Mock<IStockRepository> _mockRepo;
    private readonly StockService _service;

    public StockServiceTests()
    {
        _mockRepo = new Mock<IStockRepository>();
        _service = new StockService(_mockRepo.Object, new Mock<ILogger<StockService>>().Object);
    }

    private static InitializeStockRequest ValidRequest(params InitializeStockCategoryRequest[] categories) =>
        new(Guid.NewGuid(), null, 6, 10, false, new List<InitializeStockCategoryRequest>(categories));

    [Fact]
    public async Task InitializeAsync_NoCategories_ThrowsArgumentExceptionAndWritesNothing()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var request = ValidRequest();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.InitializeAsync(showId, request));
        _mockRepo.Verify(r => r.InitializeAsync(It.IsAny<ShowRules>(), It.IsAny<List<StockItem>>()), Times.Never);
    }

    [Fact]
    public async Task InitializeAsync_NonPositiveCapacity_ThrowsArgumentExceptionAndWritesNothing()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var request = ValidRequest(new InitializeStockCategoryRequest(Guid.NewGuid(), 0, 25.00m, "LKR", "GA"));

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.InitializeAsync(showId, request));
        _mockRepo.Verify(r => r.InitializeAsync(It.IsAny<ShowRules>(), It.IsAny<List<StockItem>>()), Times.Never);
    }

    [Fact]
    public async Task InitializeAsync_NegativePrice_ThrowsArgumentExceptionAndWritesNothing()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var request = ValidRequest(new InitializeStockCategoryRequest(Guid.NewGuid(), 100, -1.00m, "LKR", "GA"));

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.InitializeAsync(showId, request));
        _mockRepo.Verify(r => r.InitializeAsync(It.IsAny<ShowRules>(), It.IsAny<List<StockItem>>()), Times.Never);
    }

    [Fact]
    public async Task InitializeAsync_MalformedCurrency_ThrowsArgumentExceptionAndWritesNothing()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var request = ValidRequest(new InitializeStockCategoryRequest(Guid.NewGuid(), 100, 25.00m, "LK", "GA"));

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.InitializeAsync(showId, request));
        _mockRepo.Verify(r => r.InitializeAsync(It.IsAny<ShowRules>(), It.IsAny<List<StockItem>>()), Times.Never);
    }

    [Fact]
    public async Task InitializeAsync_OneInvalidCategoryAmongValidOnes_ThrowsAndWritesNothing()
    {
        // Arrange — a mix of a valid and an invalid category must reject the
        // whole request, not just skip the bad one.
        var showId = Guid.NewGuid();
        var request = ValidRequest(
            new InitializeStockCategoryRequest(Guid.NewGuid(), 100, 25.00m, "LKR", "GA"),
            new InitializeStockCategoryRequest(Guid.NewGuid(), -5, 25.00m, "LKR", "GA"));

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.InitializeAsync(showId, request));
        _mockRepo.Verify(r => r.InitializeAsync(It.IsAny<ShowRules>(), It.IsAny<List<StockItem>>()), Times.Never);
    }

    [Fact]
    public async Task InitializeAsync_ValidRequest_ReturnsRepositoryResultShape()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var request = ValidRequest(new InitializeStockCategoryRequest(categoryId, 100, 25.00m, "LKR", "GA"));
        _mockRepo.Setup(r => r.InitializeAsync(It.IsAny<ShowRules>(), It.IsAny<List<StockItem>>()))
                 .ReturnsAsync(new List<StockItem>
                 {
                     new() { ShowId = showId, CategoryId = categoryId, Capacity = 100, Available = 100, UnitPrice = 25.00m, Currency = "LKR" }
                 });

        // Act
        var result = await _service.InitializeAsync(showId, request);

        // Assert
        Assert.Single(result);
        Assert.Equal(categoryId, result[0].CategoryId);
        Assert.Equal(100, result[0].Available);
        _mockRepo.Verify(r => r.InitializeAsync(It.Is<ShowRules>(rules => rules.ShowId == showId), It.IsAny<List<StockItem>>()), Times.Once);
    }

    [Fact]
    public async Task InitializeAsync_ZeroPriceIsAllowedAndAllStockFieldsArePreserved()
    {
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var request = ValidRequest(new InitializeStockCategoryRequest(categoryId, 17, 0m, "USD", "GA"));
        _mockRepo.Setup(r => r.InitializeAsync(It.IsAny<ShowRules>(), It.IsAny<List<StockItem>>()))
            .ReturnsAsync(new List<StockItem>());

        await _service.InitializeAsync(showId, request);

        _mockRepo.Verify(r => r.InitializeAsync(
            It.Is<ShowRules>(rules => rules.ShowId == showId && rules.OrganizerId == request.OrganizerId),
            It.Is<List<StockItem>>(items => items.Count == 1 &&
                items[0].ShowId == showId && items[0].CategoryId == categoryId &&
                items[0].Capacity == 17 && items[0].UnitPrice == 0m &&
                items[0].Currency == "USD" && items[0].AllocationMode == "GA")), Times.Once);
    }

    [Fact]
    public async Task GetAvailabilityAsync_ReleasesExpiredHoldsUsingInjectedClock()
    {
        var showId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var holds = new Mock<IHoldRepository>();
        holds.Setup(r => r.ReleaseExpiredHoldsAsync(now, It.IsAny<int>()))
            .ReturnsAsync(new HoldReleaseSummary
            {
                HoldsReleased = 0,
                TicketsReturned = 0,
                QuotaClampCount = 0
            });
        _mockRepo.Setup(r => r.GetByShowIdAsync(showId)).ReturnsAsync(new List<StockItem>());
        var service = new StockService(_mockRepo.Object, new Mock<ILogger<StockService>>().Object, holds.Object, clock);

        await service.GetAvailabilityAsync(showId);

        holds.Verify(r => r.ReleaseExpiredHoldsAsync(now, It.IsAny<int>()), Times.Once);
        _mockRepo.Verify(r => r.GetByShowIdAsync(showId), Times.Once);
    }

    [Fact]
    public async Task GetAvailabilityAsync_SweepFailureStillReturnsAvailability()
    {
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var holds = new Mock<IHoldRepository>();
        holds.Setup(r => r.ReleaseExpiredHoldsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("sweep unavailable"));
        _mockRepo.Setup(r => r.GetByShowIdAsync(showId)).ReturnsAsync(new List<StockItem>
        {
            new() { ShowId = showId, CategoryId = categoryId, Capacity = 10, Available = 7, Currency = "LKR" }
        });
        var logger = new Mock<ILogger<StockService>>();
        var service = new StockService(_mockRepo.Object, logger.Object, holds.Object);

        var availability = await service.GetAvailabilityAsync(showId);

        Assert.Equal(7, Assert.Single(availability!).Available);
        logger.Verify(log => log.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(showId.ToString())),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task GetAvailabilityAsync_UnknownShow_ReturnsNull()
    {
        // Arrange
        var showId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetByShowIdAsync(showId)).ReturnsAsync(new List<StockItem>());

        // Act
        var result = await _service.GetAvailabilityAsync(showId);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAvailabilityAsync_KnownShow_ReturnsCategoryRows()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetByShowIdAsync(showId)).ReturnsAsync(new List<StockItem>
        {
            new() { ShowId = showId, CategoryId = categoryId, Capacity = 100, Available = 80, UnitPrice = 25.00m, Currency = "LKR" }
        });

        // Act
        var result = await _service.GetAvailabilityAsync(showId);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal(80, result![0].Available);
    }
}
