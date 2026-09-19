using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;
using Inventory.Service.Db;
using Inventory.Service.Models;
using Inventory.Service.Services;

namespace Inventory.Service.Tests;

public class ExpiredHoldReleaseWorkerTests
{
    private readonly Mock<IHoldRepository> _mockRepo;
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly FakeTimeProvider _timeProvider;

    public ExpiredHoldReleaseWorkerTests()
    {
        _mockRepo = new Mock<IHoldRepository>();

        var mockScope = new Mock<IServiceScope>();
        var mockServiceProvider = new Mock<IServiceProvider>();

        mockServiceProvider
            .Setup(sp => sp.GetService(typeof(IHoldRepository)))
            .Returns(_mockRepo.Object);

        mockScope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);

        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Worker_ExecutesRelease_WhenTimerFires()
    {
        _mockRepo.Setup(r => r.ReleaseExpiredHoldsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>()))
                 .ReturnsAsync(3);

        using var cts = new CancellationTokenSource();
        var worker = new ExpiredHoldReleaseWorker(
            _mockScopeFactory.Object,
            _timeProvider,
            new Mock<ILogger<ExpiredHoldReleaseWorker>>().Object,
            Options.Create(new HoldExpirySweepOptions()),
            periodOverride: TimeSpan.FromMilliseconds(50));

        var runTask = worker.StartAsync(cts.Token);
        await Task.Delay(150); // Allow worker timer tick
        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);

        _mockRepo.Verify(r => r.ReleaseExpiredHoldsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>()), Times.AtLeastOnce);
    }
}
