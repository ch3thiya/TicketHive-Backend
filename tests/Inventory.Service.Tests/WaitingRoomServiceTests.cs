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

public class WaitingRoomServiceTests
{
    private readonly Mock<IWaitingRoomRepository> _mockRepo;
    private readonly Mock<IHoldRepository> _mockHoldRepo;
    private readonly FakeTimeProvider _timeProvider;
    private readonly WaitingRoomService _service;

    public WaitingRoomServiceTests()
    {
        _mockRepo = new Mock<IWaitingRoomRepository>();
        _mockHoldRepo = new Mock<IHoldRepository>();
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 10, 0, 0, TimeSpan.Zero));
        _service = new WaitingRoomService(_mockRepo.Object, _mockHoldRepo.Object, _timeProvider, new Mock<ILogger<WaitingRoomService>>().Object);
    }

    [Fact]
    public async Task JoinQueueAsync_CustomerJoins_ReturnsWaitingStatusAndPosition()
    {
        var showId = Guid.NewGuid();
        var customerSub = "user-123";
        var entry = new WaitingRoomEntry
        {
            Id = Guid.NewGuid(),
            ShowId = showId,
            CustomerSub = customerSub,
            Status = WaitingRoomStatus.Waiting,
            Position = 1,
            CreatedAt = _timeProvider.GetUtcNow(),
            UpdatedAt = _timeProvider.GetUtcNow()
        };

        _mockRepo.Setup(r => r.JoinQueueAsync(showId, customerSub, _timeProvider.GetUtcNow()))
                 .ReturnsAsync(entry);
        _mockRepo.Setup(r => r.GetTotalWaitingAsync(showId))
                 .ReturnsAsync(1);

        var result = await _service.JoinQueueAsync(showId, customerSub);

        Assert.Equal(showId, result.ShowId);
        Assert.Equal(customerSub, result.CustomerSub);
        Assert.Equal("Waiting", result.Status);
        Assert.Equal(1, result.Position);
        Assert.Equal(1, result.TotalWaiting);
        Assert.Null(result.AdmissionToken);
    }

    [Fact]
    public async Task GetQueueStatusAsync_EntryExists_ReturnsQueueStatus()
    {
        var showId = Guid.NewGuid();
        var customerSub = "user-123";
        var entry = new WaitingRoomEntry
        {
            Id = Guid.NewGuid(),
            ShowId = showId,
            CustomerSub = customerSub,
            Status = WaitingRoomStatus.Admitted,
            Position = 1,
            AdmissionToken = "token-abc",
            TokenExpiresAt = _timeProvider.GetUtcNow().AddMinutes(10),
            CreatedAt = _timeProvider.GetUtcNow(),
            UpdatedAt = _timeProvider.GetUtcNow()
        };

        _mockRepo.Setup(r => r.GetStatusAsync(showId, customerSub, _timeProvider.GetUtcNow()))
                 .ReturnsAsync(entry);
        _mockRepo.Setup(r => r.GetTotalWaitingAsync(showId))
                 .ReturnsAsync(0);

        var result = await _service.GetQueueStatusAsync(showId, customerSub);

        Assert.NotNull(result);
        Assert.Equal("Admitted", result!.Status);
        Assert.Equal("token-abc", result.AdmissionToken);
    }

    [Fact]
    public async Task GetQueueStatusAsync_NoEntry_ReturnsNull()
    {
        var showId = Guid.NewGuid();
        var customerSub = "user-unknown";

        _mockRepo.Setup(r => r.GetStatusAsync(showId, customerSub, _timeProvider.GetUtcNow()))
                 .ReturnsAsync((WaitingRoomEntry?)null);

        var result = await _service.GetQueueStatusAsync(showId, customerSub);

        Assert.Null(result);
    }

    [Fact]
    public async Task AdmitNextCustomersAsync_AdmitsWaitingCustomers_ReturnsAdmittedCount()
    {
        var showId = Guid.NewGuid();
        var admitted = new List<WaitingRoomEntry>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ShowId = showId,
                CustomerSub = "user-1",
                Status = WaitingRoomStatus.Admitted,
                AdmissionToken = "token-1",
                TokenExpiresAt = _timeProvider.GetUtcNow().AddMinutes(10),
                CreatedAt = _timeProvider.GetUtcNow(),
                UpdatedAt = _timeProvider.GetUtcNow()
            },
            new()
            {
                Id = Guid.NewGuid(),
                ShowId = showId,
                CustomerSub = "user-2",
                Status = WaitingRoomStatus.Admitted,
                AdmissionToken = "token-2",
                TokenExpiresAt = _timeProvider.GetUtcNow().AddMinutes(10),
                CreatedAt = _timeProvider.GetUtcNow(),
                UpdatedAt = _timeProvider.GetUtcNow()
            }
        };

        _mockRepo.Setup(r => r.AdmitNextCustomersAsync(showId, 10, 10, _timeProvider.GetUtcNow()))
                 .ReturnsAsync(admitted);

        var response = await _service.AdmitNextCustomersAsync(showId, 10, 10);

        Assert.Equal(2, response.AdmittedCount);
        Assert.Contains("user-1", response.AdmittedCustomerSubs);
        Assert.Contains("user-2", response.AdmittedCustomerSubs);
    }

    [Fact]
    public async Task ValidateAdmissionTokenAsync_ValidToken_ReturnsTrue()
    {
        var showId = Guid.NewGuid();
        var customerSub = "user-1";
        var token = "valid-token";

        _mockRepo.Setup(r => r.ValidateAdmissionTokenAsync(showId, customerSub, token, _timeProvider.GetUtcNow()))
                 .ReturnsAsync(true);

        var isValid = await _service.ValidateAdmissionTokenAsync(showId, customerSub, token);

        Assert.True(isValid);
    }

    [Fact]
    public async Task ValidateAdmissionTokenAsync_InvalidOrExpiredToken_ReturnsFalse()
    {
        var showId = Guid.NewGuid();
        var customerSub = "user-1";
        var token = "expired-token";

        _mockRepo.Setup(r => r.ValidateAdmissionTokenAsync(showId, customerSub, token, _timeProvider.GetUtcNow()))
                 .ReturnsAsync(false);

        var isValid = await _service.ValidateAdmissionTokenAsync(showId, customerSub, token);

        Assert.False(isValid);
    }
}
