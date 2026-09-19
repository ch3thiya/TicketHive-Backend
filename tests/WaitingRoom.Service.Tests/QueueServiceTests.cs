using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;
using WaitingRoom.Service.Clients;
using WaitingRoom.Service.Db;
using WaitingRoom.Service.Models;
using WaitingRoom.Service.Services;

namespace WaitingRoom.Service.Tests;

public class QueueServiceTests
{
    private readonly Mock<IQueueRepository> _mockRepository;
    private readonly Mock<ICatalogClient> _mockCatalogClient;
    private readonly Mock<IAdmissionTokenIssuer> _mockTokenIssuer;
    private readonly FakeTimeProvider _timeProvider;
    private readonly QueueService _service;

    public QueueServiceTests()
    {
        _mockRepository = new Mock<IQueueRepository>();
        _mockCatalogClient = new Mock<ICatalogClient>();
        _mockTokenIssuer = new Mock<IAdmissionTokenIssuer>();
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
        var queueDefaults = Options.Create(new QueueDefaultsOptions { AdmitBatch = 50, AdmitIntervalSeconds = 30, PrequeueWindowMinutes = 30 });

        _service = new QueueService(_mockRepository.Object, _mockCatalogClient.Object, _timeProvider, _mockTokenIssuer.Object, queueDefaults);
    }

    [Fact]
    public async Task JoinAsync_NoQueueAndCatalogSaysNotHighDemand_ReturnsNull()
    {
        // Arrange
        var showId = Guid.CreateVersion7();
        _mockRepository.Setup(r => r.GetQueueAsync(showId, It.IsAny<CancellationToken>())).ReturnsAsync((Queue?)null);
        _mockCatalogClient.Setup(c => c.GetSalesRulesAsync(showId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShowSalesRules(showId, _timeProvider.GetUtcNow().AddHours(2), HighDemand: false));

        // Act
        var result = await _service.JoinAsync(showId, "customer-1");

        // Assert
        Assert.Null(result);
        _mockRepository.Verify(r => r.CreateQueueIfNotExistsAsync(
            It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task JoinAsync_NoQueueAndCatalogUnreachable_PropagatesCatalogUnavailableException()
    {
        // Arrange
        var showId = Guid.CreateVersion7();
        _mockRepository.Setup(r => r.GetQueueAsync(showId, It.IsAny<CancellationToken>())).ReturnsAsync((Queue?)null);
        _mockCatalogClient.Setup(c => c.GetSalesRulesAsync(showId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CatalogUnavailableException("Catalog is unavailable."));

        // Act & Assert
        await Assert.ThrowsAsync<CatalogUnavailableException>(() => _service.JoinAsync(showId, "customer-1"));
    }

    [Fact]
    public async Task JoinAsync_HighDemandShowNeverJoinedBefore_ProvisionsQueueThenJoinsPreQueue()
    {
        // Arrange
        var showId = Guid.CreateVersion7();
        // Ten minutes to on-sale, but the (default) 30-minute pre-queue
        // window opened twenty minutes ago — the join must proceed.
        var onSaleAt = _timeProvider.GetUtcNow().AddMinutes(10);
        var provisioned = new Queue(showId, onSaleAt, onSaleAt.AddMinutes(-30), 0, 0, 50, 30, QueueStatus.PreQueue);

        _mockRepository.Setup(r => r.GetQueueAsync(showId, It.IsAny<CancellationToken>())).ReturnsAsync((Queue?)null);
        _mockCatalogClient.Setup(c => c.GetSalesRulesAsync(showId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShowSalesRules(showId, onSaleAt, HighDemand: true));
        _mockRepository.Setup(r => r.CreateQueueIfNotExistsAsync(
                showId, onSaleAt, onSaleAt.AddMinutes(-30), 50, 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provisioned);

        var expectedEntry = new QueueEntry(showId, "customer-1", 0.42, null, _timeProvider.GetUtcNow(), null);
        _mockRepository.Setup(r => r.JoinPreQueueAsync(showId, "customer-1", _timeProvider.GetUtcNow(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedEntry);

        // Act
        var result = await _service.JoinAsync(showId, "customer-1");

        // Assert
        Assert.Same(expectedEntry, result);
        _mockRepository.Verify(r => r.CreateQueueIfNotExistsAsync(
            showId, onSaleAt, onSaleAt.AddMinutes(-30), 50, 30, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinAsync_BeforePrequeueOpens_ReturnsNull()
    {
        // Arrange
        var showId = Guid.CreateVersion7();
        var now = _timeProvider.GetUtcNow();
        var queue = new Queue(showId, now.AddHours(2), now.AddHours(1), 0, 0, 50, 30, QueueStatus.PreQueue);
        _mockRepository.Setup(r => r.GetQueueAsync(showId, It.IsAny<CancellationToken>())).ReturnsAsync(queue);

        // Act
        var result = await _service.JoinAsync(showId, "customer-1");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task JoinAsync_DuringOnSale_JoinsPostSale()
    {
        // Arrange
        var showId = Guid.CreateVersion7();
        var now = _timeProvider.GetUtcNow();
        var queue = new Queue(showId, now.AddMinutes(-1), now.AddHours(-1), 10, 11, 50, 30, QueueStatus.Open);
        _mockRepository.Setup(r => r.GetQueueAsync(showId, It.IsAny<CancellationToken>())).ReturnsAsync(queue);

        var expectedEntry = new QueueEntry(showId, "customer-1", 0.9, 11, now, null);
        _mockRepository.Setup(r => r.JoinPostSaleAsync(showId, "customer-1", now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedEntry);

        // Act
        var result = await _service.JoinAsync(showId, "customer-1");

        // Assert
        Assert.Same(expectedEntry, result);
        _mockRepository.Verify(r => r.JoinPreQueueAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetPositionAsync_NoEntry_ReturnsNotInQueue()
    {
        // Arrange
        var showId = Guid.CreateVersion7();
        _mockRepository.Setup(r => r.GetEntryAsync(showId, "customer-1", It.IsAny<CancellationToken>())).ReturnsAsync((QueueEntry?)null);

        // Act
        var result = await _service.GetPositionAsync(showId, "customer-1");

        // Assert
        Assert.Equal(QueuePositionStatus.NotInQueue, result.Status);
        Assert.Null(result.Position);
    }

    [Fact]
    public async Task GetPositionAsync_StillInPrequeue_ReturnsWaitingWithNoPositionAndTheSaleTime()
    {
        // Arrange
        var showId = Guid.CreateVersion7();
        var now = _timeProvider.GetUtcNow();
        var entry = new QueueEntry(showId, "customer-1", 0.5, null, now, null);
        var queue = new Queue(showId, now.AddHours(1), now.AddMinutes(-10), 0, 0, 50, 30, QueueStatus.PreQueue);
        _mockRepository.Setup(r => r.GetEntryAsync(showId, "customer-1", It.IsAny<CancellationToken>())).ReturnsAsync(entry);
        _mockRepository.Setup(r => r.GetQueueAsync(showId, It.IsAny<CancellationToken>())).ReturnsAsync(queue);

        // Act
        var result = await _service.GetPositionAsync(showId, "customer-1");

        // Assert
        Assert.Equal(QueuePositionStatus.Waiting, result.Status);
        Assert.Null(result.Position);
        Assert.Equal(queue.OnSaleAt, result.OnSaleAt);
    }

    [Fact]
    public async Task GetPositionAsync_QueueNumberAssigned_ReturnsPositionAsQueueNumberMinusServingNumber()
    {
        // Arrange
        var showId = Guid.CreateVersion7();
        var now = _timeProvider.GetUtcNow();
        var entry = new QueueEntry(showId, "customer-1", 0.5, 120, now, null);
        var queue = new Queue(showId, now.AddMinutes(-30), now.AddHours(-1), 70, 200, 50, 30, QueueStatus.Open);
        _mockRepository.Setup(r => r.GetEntryAsync(showId, "customer-1", It.IsAny<CancellationToken>())).ReturnsAsync(entry);
        _mockRepository.Setup(r => r.GetQueueAsync(showId, It.IsAny<CancellationToken>())).ReturnsAsync(queue);

        // Act
        var result = await _service.GetPositionAsync(showId, "customer-1");

        // Assert
        Assert.Equal(QueuePositionStatus.Waiting, result.Status);
        Assert.Equal(50, result.Position);
    }

    [Fact]
    public async Task GetPositionAsync_Admitted_ReturnsTokenFromIssuer()
    {
        // Arrange
        var showId = Guid.CreateVersion7();
        var now = _timeProvider.GetUtcNow();
        var admittedAt = now.AddMinutes(-1);
        var entry = new QueueEntry(showId, "customer-1", 0.5, 40, now.AddMinutes(-10), admittedAt);
        _mockRepository.Setup(r => r.GetEntryAsync(showId, "customer-1", It.IsAny<CancellationToken>())).ReturnsAsync(entry);
        _mockTokenIssuer.Setup(t => t.Issue(showId, "customer-1", admittedAt))
            .Returns(("signed-token", admittedAt.AddMinutes(15)));

        // Act
        var result = await _service.GetPositionAsync(showId, "customer-1");

        // Assert
        Assert.Equal(QueuePositionStatus.Admitted, result.Status);
        Assert.Equal("signed-token", result.AdmissionToken);
        Assert.Equal(admittedAt.AddMinutes(15), result.AdmissionExpiresAt);
        _mockRepository.Verify(r => r.GetQueueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetPositionAsync_QueueClosed_ReturnsSoldOut()
    {
        // Arrange
        var showId = Guid.CreateVersion7();
        var now = _timeProvider.GetUtcNow();
        var entry = new QueueEntry(showId, "customer-1", 0.5, 900, now.AddHours(-1), null);
        var queue = new Queue(showId, now.AddHours(-1), now.AddHours(-2), 500, 901, 50, 30, QueueStatus.Closed);
        _mockRepository.Setup(r => r.GetEntryAsync(showId, "customer-1", It.IsAny<CancellationToken>())).ReturnsAsync(entry);
        _mockRepository.Setup(r => r.GetQueueAsync(showId, It.IsAny<CancellationToken>())).ReturnsAsync(queue);

        // Act
        var result = await _service.GetPositionAsync(showId, "customer-1");

        // Assert
        Assert.Equal(QueuePositionStatus.SoldOut, result.Status);
        Assert.Null(result.Position);
    }
}
