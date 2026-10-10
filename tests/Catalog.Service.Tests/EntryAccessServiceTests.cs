using System;
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

public class EntryAccessServiceTests
{
    private readonly Mock<IEventRepository> _repository = new();
    private readonly Mock<IOrganizerStatusClient> _organizers = new();
    private readonly EntryAccessService _service;
    private readonly Guid _showId = Guid.NewGuid();
    private readonly Guid _eventId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();

    public EntryAccessServiceTests()
    {
        _service = new EntryAccessService(_repository.Object, _organizers.Object, NullLogger<EntryAccessService>.Instance);
    }

    private void Seed(string eventStatus = "Published", string showStatus = "Active")
    {
        _repository.Setup(r => r.GetShowByIdAsync(_showId)).ReturnsAsync(new Show { Id = _showId, EventId = _eventId, Status = showStatus });
        _repository.Setup(r => r.GetEventByIdAsync(_eventId)).ReturnsAsync(new Event { Id = _eventId, OrganizerId = _ownerId, Status = eventStatus });
    }

    private void Caller(string sub, OrganizerLookupStatus status, Guid? organizerId) =>
        _organizers.Setup(o => o.GetOrganizerStatusAsync(sub, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizerLookupResult(status, organizerId));

    [Fact]
    public async Task CheckAsync_OwningActiveOrganizer_IsAllowed()
    {
        // Arrange
        Seed();
        Caller("owner", OrganizerLookupStatus.Active, _ownerId);

        // Act
        var result = await _service.CheckAsync(_showId, "owner");

        // Assert
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task CheckAsync_OwningSuspendedOrganizer_IsStillAllowed()
    {
        // Arrange: suspension never voids tickets or stops entry.
        Seed();
        Caller("owner", OrganizerLookupStatus.Suspended, _ownerId);

        // Act
        var result = await _service.CheckAsync(_showId, "owner");

        // Assert
        Assert.True(result.IsAllowed);
    }

    [Theory]
    [InlineData("Cancelled", "Cancelled")]
    [InlineData("Draft", "Active")]
    public async Task CheckAsync_OwnerOfCancelledOrDraftShow_IsAllowedBecauseTicketStatusDecidesTheRest(string eventStatus, string showStatus)
    {
        // Arrange
        Seed(eventStatus, showStatus);
        Caller("owner", OrganizerLookupStatus.Active, _ownerId);

        // Act
        var result = await _service.CheckAsync(_showId, "owner");

        // Assert
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task CheckAsync_DifferentOrganizer_IsNotShowOwner()
    {
        // Arrange
        Seed();
        Caller("other", OrganizerLookupStatus.Active, Guid.NewGuid());

        // Act
        var result = await _service.CheckAsync(_showId, "other");

        // Assert
        Assert.Equal(EntryAccessOutcome.NotShowOwner, result.Outcome);
    }

    [Fact]
    public async Task CheckAsync_SuspendedDifferentOrganizer_IsStillNotShowOwner()
    {
        // Arrange
        Seed();
        Caller("other", OrganizerLookupStatus.Suspended, Guid.NewGuid());

        // Act
        var result = await _service.CheckAsync(_showId, "other");

        // Assert
        Assert.Equal(EntryAccessOutcome.NotShowOwner, result.Outcome);
    }

    [Fact]
    public async Task CheckAsync_CustomerOrUnknownSubject_IsNotAnOrganizer()
    {
        // Arrange
        Seed();
        Caller("customer", OrganizerLookupStatus.NotFound, null);

        // Act
        var result = await _service.CheckAsync(_showId, "customer");

        // Assert
        Assert.Equal(EntryAccessOutcome.NotAnOrganizer, result.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CheckAsync_BlankSubject_IsRefusedWithoutAskingIdentity(string sub)
    {
        // Arrange
        Seed();

        // Act
        var result = await _service.CheckAsync(_showId, sub);

        // Assert
        Assert.Equal(EntryAccessOutcome.NotAnOrganizer, result.Outcome);
        _organizers.Verify(o => o.GetOrganizerStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_IdentityUnavailable_FailsClosed()
    {
        // Arrange
        Seed();
        Caller("owner", OrganizerLookupStatus.Unavailable, null);

        // Act
        var result = await _service.CheckAsync(_showId, "owner");

        // Assert
        Assert.Equal(EntryAccessOutcome.OrganizerStatusUnavailable, result.Outcome);
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task CheckAsync_UnknownShow_ReturnsShowNotFoundWithoutAskingIdentity()
    {
        // Arrange
        _repository.Setup(r => r.GetShowByIdAsync(_showId)).ReturnsAsync((Show?)null);

        // Act
        var result = await _service.CheckAsync(_showId, "owner");

        // Assert
        Assert.Equal(EntryAccessOutcome.ShowNotFound, result.Outcome);
        _organizers.Verify(o => o.GetOrganizerStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_RepeatedCalls_AskIdentityEveryTime()
    {
        // Arrange: ownership is never cached.
        Seed();
        Caller("owner", OrganizerLookupStatus.Active, _ownerId);

        // Act
        await _service.CheckAsync(_showId, "owner");
        await _service.CheckAsync(_showId, "owner");

        // Assert
        _organizers.Verify(o => o.GetOrganizerStatusAsync("owner", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}