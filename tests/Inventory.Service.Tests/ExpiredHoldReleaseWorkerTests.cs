using System;
using System.Diagnostics.Metrics;
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

    // This is the exact blind spot that hid the available_quantity bug for
    // a whole sprint: a repository call that succeeds proves nothing about
    // whether the worker's loop survives a real failure. A mock can't reach
    // SQL, so what belongs here is the worker-level guarantee that a thrown
    // exception delays a pass but never stops the sweeper (ADR-008) —
    // ReleaseExpiredHoldsAsync's own SQL correctness is proven separately
    // against real Postgres in HoldExpiryReleaseTests/HoldExpiryConcurrencyTests.
    [Fact]
    public async Task Worker_RepositoryThrowsOnFirstPass_SurvivesAndCallsAgainOnNextPass()
    {
        var callCount = 0;
        var secondCallCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _mockRepo.Setup(r => r.ReleaseExpiredHoldsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>()))
                 .Returns(() =>
                 {
                     if (Interlocked.Increment(ref callCount) == 1)
                     {
                         throw new InvalidOperationException("Simulated transient failure.");
                     }

                     secondCallCompleted.TrySetResult();
                     return Task.FromResult(new HoldReleaseSummary { HoldsReleased = 2, TicketsReturned = 4, QuotaClampCount = 0 });
                 });

        using var cts = new CancellationTokenSource();
        var worker = new ExpiredHoldReleaseWorker(
            _mockScopeFactory.Object,
            _timeProvider,
            new Mock<ILogger<ExpiredHoldReleaseWorker>>().Object,
            Options.Create(new HoldExpirySweepOptions()),
            new HoldExpiryMetrics(new Meter(nameof(ExpiredHoldReleaseWorkerTests))),
            periodOverride: TimeSpan.FromMilliseconds(20));

        // Act
        var runTask = worker.StartAsync(cts.Token);
        var completed = await Task.WhenAny(secondCallCompleted.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);

        // Assert — the loop survived the first pass's exception and reached
        // a second pass instead of crashing or going quiet after the first.
        Assert.Same(secondCallCompleted.Task, completed);
        Assert.True(callCount >= 2, $"Expected at least 2 calls after the first one threw, got {callCount}.");
    }
}
