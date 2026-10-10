using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;
using Inventory.Service.Clients;
using Inventory.Service.Db;
using Inventory.Service.Models;
using Inventory.Service.Services;

namespace Inventory.Service.Tests;

public class HoldServiceSalesEligibilityTests
{
    private readonly Mock<IHoldRepository> _repository = new();
    private readonly Mock<IAdmissionTokenVerifier> _verifier = new();
    private readonly Mock<ISalesEligibilityClient> _eligibility = new();
    private readonly HoldService _service;
    private readonly Guid _showId = Guid.NewGuid();
    private readonly CreateHoldRequest _request;

    public HoldServiceSalesEligibilityTests()
    {
        _service = new HoldService(_repository.Object, new FakeTimeProvider(new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero)),
            NullLogger<HoldService>.Instance, _verifier.Object, _eligibility.Object);
        _request = new CreateHoldRequest(_showId, [new CreateHoldItemRequest(Guid.NewGuid(), 1)]);
        _repository.Setup(r => r.GetShowRulesAsync(_showId)).ReturnsAsync(Rules());
    }

    private ShowRules Rules(bool highDemand = false, int threshold = 0) => new()
    {
        ShowId = _showId,
        OrganizerId = Guid.NewGuid(),
        MaxPerCustomer = 6,
        HoldMinutes = 10,
        HighDemand = highDemand,
        HighDemandThreshold = threshold
    };

    private void Eligibility(SalesEligibilityStatus status) =>
        _eligibility.Setup(e => e.CheckAsync(_showId, It.IsAny<CancellationToken>())).ReturnsAsync(new SalesEligibilityDecision(status));

    [Theory]
    [InlineData(SalesEligibilityStatus.OrganizerSuspended, CreateHoldStatus.OrganizerSuspended)]
    [InlineData(SalesEligibilityStatus.NotOnSale, CreateHoldStatus.ShowNotOnSale)]
    [InlineData(SalesEligibilityStatus.Unavailable, CreateHoldStatus.SalesEligibilityUnavailable)]
    public async Task CreateHoldAsync_IneligibleShow_ReturnsMatchingStatusAndWritesNothing(SalesEligibilityStatus eligibility, CreateHoldStatus expected)
    {
        // Arrange
        Eligibility(eligibility);

        // Act
        var result = await _service.CreateHoldAsync("customer", "key", false, _request);

        // Assert
        Assert.Equal(expected, result.Status);
        _repository.Verify(r => r.CreateAsync(It.IsAny<Hold>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task CreateHoldAsync_EligibleShow_CreatesTheHold()
    {
        // Arrange
        Eligibility(SalesEligibilityStatus.Eligible);
        _repository.Setup(r => r.CreateAsync(It.IsAny<Hold>(), 6)).ReturnsAsync((Hold h, int _) =>
            new HoldCreationResult { Outcome = HoldCreationOutcome.Created, Hold = h });

        // Act
        var result = await _service.CreateHoldAsync("customer", "key", false, _request);

        // Assert
        Assert.Equal(CreateHoldStatus.Created, result.Status);
    }

    [Fact]
    public async Task CreateHoldAsync_SuspendedButSameKeyAlreadyHeld_ReplaysTheOriginalHold()
    {
        // Arrange
        Eligibility(SalesEligibilityStatus.OrganizerSuspended);
        var existing = new Hold
        {
            Id = Guid.NewGuid(),
            ShowId = _showId,
            CustomerSub = "customer",
            Status = HoldStatus.Active,
            IdempotencyKey = "key",
            ExpiresAt = new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero),
            Items = new List<HoldItem>()
        };
        _repository.Setup(r => r.GetByIdempotencyKeyAsync("customer", "key")).ReturnsAsync(existing);

        // Act
        var result = await _service.CreateHoldAsync("customer", "key", false, _request);

        // Assert
        Assert.Equal(CreateHoldStatus.Duplicate, result.Status);
        Assert.Equal(existing.Id, result.Hold!.HoldId);
    }

    [Fact]
    public async Task CreateHoldAsync_HighDemandGateBlocks_NeverCallsCatalog()
    {
        // Arrange
        _repository.Setup(r => r.GetShowRulesAsync(_showId)).ReturnsAsync(Rules(highDemand: true, threshold: 0));
        _repository.Setup(r => r.GetActiveHoldCountAsync(_showId, It.IsAny<DateTimeOffset>())).ReturnsAsync(5);

        // Act
        var result = await _service.CreateHoldAsync("customer", "key", false, _request);

        // Assert: unadmitted traffic is turned away locally, protecting Catalog and Identity during an on-sale.
        Assert.Equal(CreateHoldStatus.HighDemandBlocked, result.Status);
        _eligibility.Verify(e => e.CheckAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateHoldAsync_UnknownShow_NeverCallsCatalog()
    {
        // Arrange
        _repository.Setup(r => r.GetShowRulesAsync(_showId)).ReturnsAsync((ShowRules?)null);

        // Act
        var result = await _service.CreateHoldAsync("customer", "key", false, _request);

        // Assert
        Assert.Equal(CreateHoldStatus.ShowNotFound, result.Status);
        _eligibility.Verify(e => e.CheckAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}