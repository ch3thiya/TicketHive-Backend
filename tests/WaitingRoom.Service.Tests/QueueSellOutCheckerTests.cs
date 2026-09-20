using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using WaitingRoom.Service.Clients;
using WaitingRoom.Service.Db;
using WaitingRoom.Service.Services;

namespace WaitingRoom.Service.Tests;

public class QueueSellOutCheckerTests
{
    private static (Mock<IQueueRepository> Repository, Mock<IInventoryClient> InventoryClient, Mock<IServiceScopeFactory> ScopeFactory) CreateMocks()
    {
        var mockRepository = new Mock<IQueueRepository>();
        var mockInventoryClient = new Mock<IInventoryClient>();

        var mockServiceProvider = new Mock<IServiceProvider>();
        mockServiceProvider.Setup(sp => sp.GetService(typeof(IQueueRepository))).Returns(mockRepository.Object);
        mockServiceProvider.Setup(sp => sp.GetService(typeof(IInventoryClient))).Returns(mockInventoryClient.Object);

        var mockScope = new Mock<IServiceScope>();
        mockScope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);

        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        return (mockRepository, mockInventoryClient, mockScopeFactory);
    }

    [Fact]
    public async Task Checker_ShowIsSoldOut_ClosesTheQueue()
    {
        // Arrange
        var (mockRepository, mockInventoryClient, mockScopeFactory) = CreateMocks();
        var showId = Guid.CreateVersion7();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        mockRepository.Setup(r => r.GetActiveShowIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Guid> { showId });
        mockInventoryClient.Setup(c => c.IsSoldOutAsync(showId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mockRepository.Setup(r => r.CloseQueueAsync(showId, It.IsAny<CancellationToken>()))
            .Callback(() => closed.TrySetResult())
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        var checker = new QueueSellOutChecker(
            mockScopeFactory.Object,
            Mock.Of<ILogger<QueueSellOutChecker>>(),
            Options.Create(new QueueSellOutOptions()),
            periodOverride: TimeSpan.FromMilliseconds(20));

        // Act
        var runTask = checker.StartAsync(cts.Token);
        var completed = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        cts.Cancel();
        await checker.StopAsync(CancellationToken.None);

        // Assert
        Assert.Same(closed.Task, completed);
        mockRepository.Verify(r => r.CloseQueueAsync(showId, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Checker_InventoryUnavailable_NeverClosesTheQueueAndKeepsRunning()
    {
        // Arrange
        var (mockRepository, mockInventoryClient, mockScopeFactory) = CreateMocks();
        var showId = Guid.CreateVersion7();
        var secondAttemptMade = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;

        mockRepository.Setup(r => r.GetActiveShowIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Guid> { showId });
        mockInventoryClient.Setup(c => c.IsSoldOutAsync(showId, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref attempts) >= 2)
                {
                    secondAttemptMade.TrySetResult();
                }

                return Task.FromException<bool>(new InventoryUnavailableException("Inventory is unavailable."));
            });

        using var cts = new CancellationTokenSource();
        var checker = new QueueSellOutChecker(
            mockScopeFactory.Object,
            Mock.Of<ILogger<QueueSellOutChecker>>(),
            Options.Create(new QueueSellOutOptions()),
            periodOverride: TimeSpan.FromMilliseconds(20));

        // Act — the checker must survive the failure and try again next tick
        var runTask = checker.StartAsync(cts.Token);
        var completed = await Task.WhenAny(secondAttemptMade.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        cts.Cancel();
        await checker.StopAsync(CancellationToken.None);

        // Assert — never closed because a health check failed
        Assert.Same(secondAttemptMade.Task, completed);
        mockRepository.Verify(r => r.CloseQueueAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
