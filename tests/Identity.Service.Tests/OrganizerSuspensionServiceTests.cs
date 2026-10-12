using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;
using Identity.Service.Db;
using Identity.Service.Models;
using Identity.Service.Services;

namespace Identity.Service.Tests;

public class OrganizerSuspensionServiceTests
{
    private readonly Mock<IAccountRepository> _repository = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero));
    private readonly OrganizerSuspensionService _service;

    public OrganizerSuspensionServiceTests()
    {
        _service = new OrganizerSuspensionService(_repository.Object, _time, NullLogger<OrganizerSuspensionService>.Instance);
    }

    private void SetupApply(OrganizerStatusChangeOutcome outcome, string? status = "suspended") =>
        _repository
            .Setup(r => r.ApplyOrganizerStatusChangeAsync(It.IsAny<Guid>(), It.IsAny<OrganizerStatusAction>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(new OrganizerStatusChangeResult(outcome, status));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SuspendAsync_MissingReason_ReturnsInvalidReasonWithoutWriting(string? reason)
    {
        // Arrange
        var organizerId = Guid.NewGuid();

        // Act
        var result = await _service.SuspendAsync(organizerId, "admin-sub", reason);

        // Assert
        Assert.Equal(OrganizerStatusChangeOutcome.InvalidReason, result.Outcome);
        _repository.Verify(r => r.ApplyOrganizerStatusChangeAsync(
            It.IsAny<Guid>(), It.IsAny<OrganizerStatusAction>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    [Fact]
    public async Task SuspendAsync_ReasonTooLong_ReturnsInvalidReason()
    {
        // Arrange
        var reason = new string('x', OrganizerSuspensionService.MaxReasonLength + 1);

        // Act
        var result = await _service.SuspendAsync(Guid.NewGuid(), "admin-sub", reason);

        // Assert
        Assert.Equal(OrganizerStatusChangeOutcome.InvalidReason, result.Outcome);
    }

    [Fact]
    public async Task SuspendAsync_ValidReason_RecordsTrimmedReasonActorAndTimeProviderTimestamp()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        SetupApply(OrganizerStatusChangeOutcome.Changed);

        // Act
        var result = await _service.SuspendAsync(organizerId, "admin-sub", "  Chargeback abuse  ");

        // Assert
        Assert.Equal(OrganizerStatusChangeOutcome.Changed, result.Outcome);
        _repository.Verify(r => r.ApplyOrganizerStatusChangeAsync(
            organizerId, OrganizerStatusAction.Suspend, "admin-sub", "Chargeback abuse", _time.GetUtcNow()), Times.Once);
    }

    [Fact]
    public async Task ReinstateAsync_NoNote_UsesDefaultNote()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        SetupApply(OrganizerStatusChangeOutcome.Changed, "approved");

        // Act
        await _service.ReinstateAsync(organizerId, "admin-sub", null);

        // Assert
        _repository.Verify(r => r.ApplyOrganizerStatusChangeAsync(
            organizerId, OrganizerStatusAction.Reinstate, "admin-sub", OrganizerSuspensionService.DefaultReinstatementNote, It.IsAny<DateTimeOffset>()), Times.Once);
    }

    [Fact]
    public async Task GetHistoryAsync_UnknownOrganizer_ReturnsNull()
    {
        // Arrange
        _repository.Setup(r => r.GetUserAccountByIdAsync(It.IsAny<Guid>())).ReturnsAsync((UserAccount?)null);

        // Act
        var history = await _service.GetHistoryAsync(Guid.NewGuid());

        // Assert
        Assert.Null(history);
    }

    [Fact]
    public async Task GetHistoryAsync_NonOrganizerAccount_ReturnsNull()
    {
        // Arrange
        var id = Guid.NewGuid();
        _repository.Setup(r => r.GetUserAccountByIdAsync(id)).ReturnsAsync(new UserAccount { Id = id, Role = "Customer" });

        // Act
        var history = await _service.GetHistoryAsync(id);

        // Assert
        Assert.Null(history);
    }
}