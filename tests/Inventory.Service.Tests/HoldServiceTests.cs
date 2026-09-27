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

public class HoldServiceTests
{
    private readonly Mock<IHoldRepository> _mockRepo;
    private readonly Mock<IAdmissionTokenVerifier> _mockVerifier;
    private readonly FakeTimeProvider _timeProvider;
    private readonly HoldService _service;

    public HoldServiceTests()
    {
        _mockRepo = new Mock<IHoldRepository>();
        _mockVerifier = new Mock<IAdmissionTokenVerifier>();
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 10, 0, 0, TimeSpan.Zero));
        _service = new HoldService(_mockRepo.Object, _timeProvider, new Mock<ILogger<HoldService>>().Object, _mockVerifier.Object);
    }

    private static CreateHoldRequest ValidRequest(params CreateHoldItemRequest[] items) =>
        new(Guid.NewGuid(), new List<CreateHoldItemRequest>(items));

    private ShowRules ActiveShowRules(int maxPerCustomer = 6, int holdMinutes = 10, bool highDemand = false, int? highDemandThreshold = null) => new()
    {
        ShowId = Guid.NewGuid(),
        OrganizerId = Guid.NewGuid(),
        MaxPerCustomer = maxPerCustomer,
        HoldMinutes = holdMinutes,
        HighDemand = highDemand,
        HighDemandThreshold = highDemandThreshold
    };

    [Fact]
    public async Task CreateHoldAsync_EmptyItems_ThrowsArgumentExceptionAndWritesNothing()
    {
        var request = ValidRequest();

        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateHoldAsync("sub", "key", false, request));
        _mockRepo.Verify(r => r.CreateAsync(It.IsAny<Hold>(), It.IsAny<int>()), Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CreateHoldAsync_NonPositiveQuantity_ThrowsArgumentExceptionAndWritesNothing(int quantity)
    {
        var request = ValidRequest(new CreateHoldItemRequest(Guid.NewGuid(), quantity));

        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateHoldAsync("sub", "key", false, request));
        _mockRepo.Verify(r => r.CreateAsync(It.IsAny<Hold>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task CreateHoldAsync_OneInvalidItemAmongValidOnes_ThrowsAndWritesNothing()
    {
        var request = ValidRequest(
            new CreateHoldItemRequest(Guid.NewGuid(), 2),
            new CreateHoldItemRequest(Guid.NewGuid(), 0));

        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateHoldAsync("sub", "key", false, request));
        _mockRepo.Verify(r => r.CreateAsync(It.IsAny<Hold>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task CreateHoldAsync_UnknownShow_ReturnsShowNotFoundAndWritesNothing()
    {
        var request = ValidRequest(new CreateHoldItemRequest(Guid.NewGuid(), 1));
        _mockRepo.Setup(r => r.GetShowRulesAsync(request.ShowId)).ReturnsAsync((ShowRules?)null);

        var result = await _service.CreateHoldAsync("sub", "key", false, request);

        Assert.Equal(CreateHoldStatus.ShowNotFound, result.Status);
        _mockRepo.Verify(r => r.CreateAsync(It.IsAny<Hold>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task CreateHoldAsync_HighDemandShowWithoutAdmissionToken_ReturnsHighDemandBlockedBeforeTouchingStock()
    {
        var showRules = ActiveShowRules(highDemand: true, highDemandThreshold: 0);
        var request = new CreateHoldRequest(showRules.ShowId, new List<CreateHoldItemRequest> { new(Guid.NewGuid(), 1) });
        _mockRepo.Setup(r => r.GetShowRulesAsync(showRules.ShowId)).ReturnsAsync(showRules);
        _mockRepo.Setup(r => r.GetActiveHoldCountAsync(showRules.ShowId, It.IsAny<DateTimeOffset>())).ReturnsAsync(0);

        var result = await _service.CreateHoldAsync("sub", "key", hasAdmissionToken: false, request);

        Assert.Equal(CreateHoldStatus.HighDemandBlocked, result.Status);
        _mockRepo.Verify(r => r.CreateAsync(It.IsAny<Hold>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task CreateHoldAsync_HighDemandShowWithActiveHoldsAtOrAboveThreshold_RequiresAdmissionToken()
    {
        var showRules = ActiveShowRules(highDemand: true, highDemandThreshold: 500);
        var request = new CreateHoldRequest(showRules.ShowId, new List<CreateHoldItemRequest> { new(Guid.NewGuid(), 1) });
        _mockRepo.Setup(r => r.GetShowRulesAsync(showRules.ShowId)).ReturnsAsync(showRules);
        _mockRepo.Setup(r => r.GetActiveHoldCountAsync(showRules.ShowId, It.IsAny<DateTimeOffset>())).ReturnsAsync(500);

        var result = await _service.CreateHoldAsync("sub", "key", hasAdmissionToken: false, request);

        Assert.Equal(CreateHoldStatus.HighDemandBlocked, result.Status);
        _mockRepo.Verify(r => r.CreateAsync(It.IsAny<Hold>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task CreateHoldAsync_HighDemandShowWithActiveHoldsBelowThreshold_BypassesAdmissionToken()
    {
        var showRules = ActiveShowRules(highDemand: true, highDemandThreshold: 500);
        var request = new CreateHoldRequest(showRules.ShowId, new List<CreateHoldItemRequest> { new(Guid.NewGuid(), 1) });
        _mockRepo.Setup(r => r.GetShowRulesAsync(showRules.ShowId)).ReturnsAsync(showRules);
        _mockRepo.Setup(r => r.GetActiveHoldCountAsync(showRules.ShowId, It.IsAny<DateTimeOffset>())).ReturnsAsync(0);
        _mockRepo.Setup(r => r.CreateAsync(It.IsAny<Hold>(), showRules.MaxPerCustomer))
                 .ReturnsAsync(new HoldCreationResult { Outcome = HoldCreationOutcome.Created, Hold = HoldWithItem(showRules.ShowId) });

        var result = await _service.CreateHoldAsync("sub", "key", hasAdmissionToken: false, request);

        Assert.Equal(CreateHoldStatus.Created, result.Status);
        _mockRepo.Verify(r => r.CreateAsync(It.IsAny<Hold>(), showRules.MaxPerCustomer), Times.Once);
    }

    [Fact]
    public async Task CreateHoldAsync_HighDemandShowWithInvalidToken_ReturnsHighDemandBlocked()
    {
        var showRules = ActiveShowRules(highDemand: true, highDemandThreshold: 0);
        var request = new CreateHoldRequest(showRules.ShowId, new List<CreateHoldItemRequest> { new(Guid.NewGuid(), 1) });
        _mockRepo.Setup(r => r.GetShowRulesAsync(showRules.ShowId)).ReturnsAsync(showRules);
        _mockRepo.Setup(r => r.GetActiveHoldCountAsync(showRules.ShowId, It.IsAny<DateTimeOffset>())).ReturnsAsync(0);
        _mockVerifier.Setup(v => v.Verify(showRules.ShowId, "sub", "bad-token")).Returns(false);

        var result = await _service.CreateHoldAsync("sub", "key", hasAdmissionToken: true, request, admissionToken: "bad-token");

        Assert.Equal(CreateHoldStatus.HighDemandBlocked, result.Status);
        _mockRepo.Verify(r => r.CreateAsync(It.IsAny<Hold>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task CreateHoldAsync_HighDemandShowWithValidToken_ProceedsToAllocation()
    {
        var showRules = ActiveShowRules(highDemand: true, highDemandThreshold: 0);
        var request = new CreateHoldRequest(showRules.ShowId, new List<CreateHoldItemRequest> { new(Guid.NewGuid(), 1) });
        _mockRepo.Setup(r => r.GetShowRulesAsync(showRules.ShowId)).ReturnsAsync(showRules);
        _mockRepo.Setup(r => r.GetActiveHoldCountAsync(showRules.ShowId, It.IsAny<DateTimeOffset>())).ReturnsAsync(0);
        _mockVerifier.Setup(v => v.Verify(showRules.ShowId, "sub", "valid-token")).Returns(true);
        _mockRepo.Setup(r => r.CreateAsync(It.IsAny<Hold>(), showRules.MaxPerCustomer))
                 .ReturnsAsync(new HoldCreationResult { Outcome = HoldCreationOutcome.Created, Hold = HoldWithItem(showRules.ShowId) });

        var result = await _service.CreateHoldAsync("sub", "key", hasAdmissionToken: true, request, admissionToken: "valid-token");

        Assert.Equal(CreateHoldStatus.Created, result.Status);
        _mockRepo.Verify(r => r.CreateAsync(It.IsAny<Hold>(), showRules.MaxPerCustomer), Times.Once);
    }

    [Fact]
    public async Task CreateHoldAsync_ExpiresAtIsCreatedAtPlusHoldMinutesFromTimeProvider()
    {
        var showRules = ActiveShowRules(holdMinutes: 15);
        var request = new CreateHoldRequest(showRules.ShowId, new List<CreateHoldItemRequest> { new(Guid.NewGuid(), 1) });
        _mockRepo.Setup(r => r.GetShowRulesAsync(showRules.ShowId)).ReturnsAsync(showRules);

        Hold? capturedHold = null;
        _mockRepo.Setup(r => r.CreateAsync(It.IsAny<Hold>(), showRules.MaxPerCustomer))
                 .Callback<Hold, int>((hold, _) => capturedHold = hold)
                 .ReturnsAsync(new HoldCreationResult { Outcome = HoldCreationOutcome.Created, Hold = HoldWithItem(showRules.ShowId) });

        await _service.CreateHoldAsync("sub", "key", false, request);

        Assert.NotNull(capturedHold);
        Assert.Equal(_timeProvider.GetUtcNow(), capturedHold!.CreatedAt);
        Assert.Equal(_timeProvider.GetUtcNow().AddMinutes(15), capturedHold.ExpiresAt);
    }

    [Fact]
    public async Task CreateHoldAsync_QuotaExceeded_ReturnsQuotaExceededWithLimit()
    {
        var showRules = ActiveShowRules(maxPerCustomer: 6);
        var request = new CreateHoldRequest(showRules.ShowId, new List<CreateHoldItemRequest> { new(Guid.NewGuid(), 2) });
        _mockRepo.Setup(r => r.GetShowRulesAsync(showRules.ShowId)).ReturnsAsync(showRules);
        _mockRepo.Setup(r => r.CreateAsync(It.IsAny<Hold>(), showRules.MaxPerCustomer))
                 .ReturnsAsync(new HoldCreationResult { Outcome = HoldCreationOutcome.QuotaExceeded, Limit = 6 });

        var result = await _service.CreateHoldAsync("sub", "key", false, request);

        Assert.Equal(CreateHoldStatus.QuotaExceeded, result.Status);
        Assert.Equal(6, result.Limit);
    }

    [Fact]
    public async Task CreateHoldAsync_StockUnavailable_ReturnsStockUnavailableWithCategoryId()
    {
        var showRules = ActiveShowRules();
        var categoryId = Guid.NewGuid();
        var request = new CreateHoldRequest(showRules.ShowId, new List<CreateHoldItemRequest> { new(categoryId, 2) });
        _mockRepo.Setup(r => r.GetShowRulesAsync(showRules.ShowId)).ReturnsAsync(showRules);
        _mockRepo.Setup(r => r.CreateAsync(It.IsAny<Hold>(), showRules.MaxPerCustomer))
                 .ReturnsAsync(new HoldCreationResult { Outcome = HoldCreationOutcome.StockUnavailable, CategoryId = categoryId });

        var result = await _service.CreateHoldAsync("sub", "key", false, request);

        Assert.Equal(CreateHoldStatus.StockUnavailable, result.Status);
        Assert.Equal(categoryId, result.CategoryId);
    }

    [Fact]
    public async Task CreateHoldAsync_Duplicate_ReturnsOriginalHold()
    {
        var showRules = ActiveShowRules();
        var request = new CreateHoldRequest(showRules.ShowId, new List<CreateHoldItemRequest> { new(Guid.NewGuid(), 2) });
        var original = HoldWithItem(showRules.ShowId);
        _mockRepo.Setup(r => r.GetShowRulesAsync(showRules.ShowId)).ReturnsAsync(showRules);
        _mockRepo.Setup(r => r.CreateAsync(It.IsAny<Hold>(), showRules.MaxPerCustomer))
                 .ReturnsAsync(new HoldCreationResult { Outcome = HoldCreationOutcome.Duplicate, Hold = original });

        var result = await _service.CreateHoldAsync("sub", "key", false, request);

        Assert.Equal(CreateHoldStatus.Duplicate, result.Status);
        Assert.Equal(original.Id, result.Hold!.HoldId);
    }

    [Fact]
    public async Task GetHoldAsync_DelegatesToRepository()
    {
        var holdId = Guid.NewGuid();
        var hold = HoldWithItem(Guid.NewGuid());
        _mockRepo.Setup(r => r.GetByIdAsync(holdId)).ReturnsAsync(hold);

        var result = await _service.GetHoldAsync(holdId);

        Assert.Same(hold, result);
    }

    private static Hold HoldWithItem(Guid showId) => new()
    {
        Id = Guid.NewGuid(),
        ShowId = showId,
        CustomerSub = "sub",
        Status = HoldStatus.Active,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        IdempotencyKey = "key",
        CreatedAt = DateTimeOffset.UtcNow,
        Items = new List<HoldItem> { new() { CategoryId = Guid.NewGuid(), Quantity = 1, UnitPrice = 50.00m, Currency = "LKR" } }
    };
}
