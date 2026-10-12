using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Catalog.Service.Clients;
using Catalog.Service.Db;
using Catalog.Service.Models;
using Catalog.Service.Services;

namespace Catalog.Service.Tests;

public class SalesEligibilityServiceTests
{
    private readonly Mock<IEventRepository> _repository = new();
    private readonly Mock<IOrganizerStatusClient> _organizers = new();
    private readonly SalesEligibilityService _service;
    private readonly Guid _showId = Guid.NewGuid();
    private readonly Guid _eventId = Guid.NewGuid();
    private readonly Guid _organizerId = Guid.NewGuid();

    public SalesEligibilityServiceTests()
    {
        _service = new SalesEligibilityService(_repository.Object, _organizers.Object, NullLogger<SalesEligibilityService>.Instance);
    }

    private void Seed(string eventStatus = "Published", string showStatus = "Active")
    {
        _repository.Setup(r => r.GetShowByIdAsync(_showId)).ReturnsAsync(new Show { Id = _showId, EventId = _eventId, Status = showStatus });
        _repository.Setup(r => r.GetEventByIdAsync(_eventId)).ReturnsAsync(new Event { Id = _eventId, OrganizerId = _organizerId, Status = eventStatus });
    }

    private void OrganizerIs(OrganizerLookupStatus status) =>
        _organizers.Setup(o => o.GetOrganizerStatusByIdAsync(_organizerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizerLookupResult(status, _organizerId));

    [Fact]
    public async Task CheckAsync_PublishedActiveShowOfActiveOrganizer_IsEligible()
    {
        // Arrange
        Seed();
        OrganizerIs(OrganizerLookupStatus.Active);

        // Act
        var result = await _service.CheckAsync(_showId);

        // Assert
        Assert.True(result.IsEligible);
    }

    [Fact]
    public async Task CheckAsync_SuspendedOrganizer_IsNotEligible()
    {
        // Arrange
        Seed();
        OrganizerIs(OrganizerLookupStatus.Suspended);

        // Act
        var result = await _service.CheckAsync(_showId);

        // Assert
        Assert.Equal(SalesEligibilityOutcome.OrganizerSuspended, result.Outcome);
    }

    [Fact]
    public async Task CheckAsync_IdentityUnavailable_FailsClosed()
    {
        // Arrange
        Seed();
        OrganizerIs(OrganizerLookupStatus.Unavailable);

        // Act
        var result = await _service.CheckAsync(_showId);

        // Assert
        Assert.Equal(SalesEligibilityOutcome.OrganizerStatusUnavailable, result.Outcome);
        Assert.False(result.IsEligible);
    }

    [Fact]
    public async Task CheckAsync_OrganizerUnknownToIdentity_IsNotEligible()
    {
        // Arrange
        Seed();
        _organizers.Setup(o => o.GetOrganizerStatusByIdAsync(_organizerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizerLookupResult(OrganizerLookupStatus.NotFound, null));

        // Act
        var result = await _service.CheckAsync(_showId);

        // Assert
        Assert.Equal(SalesEligibilityOutcome.OrganizerNotActive, result.Outcome);
    }

    [Theory]
    [InlineData("Draft", "Active")]
    [InlineData("Cancelled", "Active")]
    [InlineData("Published", "Cancelled")]
    [InlineData("Cancelled", "Cancelled")]
    public async Task CheckAsync_DraftOrCancelledEventOrShow_IsNeverSellableEvenForActiveOrganizer(string eventStatus, string showStatus)
    {
        // Arrange
        Seed(eventStatus, showStatus);
        OrganizerIs(OrganizerLookupStatus.Active);

        // Act
        var result = await _service.CheckAsync(_showId);

        // Assert
        Assert.Equal(SalesEligibilityOutcome.ShowNotOnSale, result.Outcome);
    }

    [Fact]
    public async Task CheckAsync_NonSellableShow_DoesNotAskIdentity()
    {
        // Arrange
        Seed("Draft");

        // Act
        await _service.CheckAsync(_showId);

        // Assert
        _organizers.Verify(o => o.GetOrganizerStatusByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_UnknownShow_ReturnsShowNotFound()
    {
        // Arrange
        _repository.Setup(r => r.GetShowByIdAsync(_showId)).ReturnsAsync((Show?)null);

        // Act
        var result = await _service.CheckAsync(_showId);

        // Assert
        Assert.Equal(SalesEligibilityOutcome.ShowNotFound, result.Outcome);
    }

    [Fact]
    public async Task CheckAsync_SuspendThenReinstate_FollowsIdentityOnEveryCallWithoutRestart()
    {
        // Arrange
        Seed();
        var statuses = new Queue<OrganizerLookupStatus>([OrganizerLookupStatus.Active, OrganizerLookupStatus.Suspended, OrganizerLookupStatus.Active]);
        _organizers.Setup(o => o.GetOrganizerStatusByIdAsync(_organizerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new OrganizerLookupResult(statuses.Dequeue(), _organizerId));

        // Act
        var before = await _service.CheckAsync(_showId);
        var during = await _service.CheckAsync(_showId);
        var after = await _service.CheckAsync(_showId);

        // Assert
        Assert.Equal(
            new[] { SalesEligibilityOutcome.Eligible, SalesEligibilityOutcome.OrganizerSuspended, SalesEligibilityOutcome.Eligible },
            new[] { before.Outcome, during.Outcome, after.Outcome });
    }

    [Fact]
    public async Task CheckAsync_ReinstatedOrganizerWithCancelledShow_StaysUnsellable()
    {
        // Arrange
        Seed(showStatus: "Cancelled");
        OrganizerIs(OrganizerLookupStatus.Active);

        // Act
        var result = await _service.CheckAsync(_showId);

        // Assert
        Assert.Equal(SalesEligibilityOutcome.ShowNotOnSale, result.Outcome);
    }

    [Fact]
    public async Task GetSuspendedOrganizerIdsAsync_ReturnsOnlySuspendedOrganizers()
    {
        // Arrange
        var suspended = Guid.NewGuid();
        var active = Guid.NewGuid();
        var unavailable = Guid.NewGuid();
        _organizers.Setup(o => o.GetOrganizerStatusesAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, OrganizerLookupStatus>
            {
                [suspended] = OrganizerLookupStatus.Suspended,
                [active] = OrganizerLookupStatus.Active,
                [unavailable] = OrganizerLookupStatus.Unavailable
            });

        // Act
        var result = await _service.GetSuspendedOrganizerIdsAsync([suspended, active, unavailable]);

        // Assert
        Assert.Equal(new[] { suspended }, result.ToArray());
    }
}