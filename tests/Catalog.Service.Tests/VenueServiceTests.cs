using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;
using Catalog.Service.Db;
using Catalog.Service.Models;
using Catalog.Service.Services;

namespace Catalog.Service.Tests;

public class VenueServiceTests
{
    private readonly Mock<IVenueRepository> _mockRepo;
    private readonly FakeTimeProvider _timeProvider;
    private readonly Mock<ILogger<VenueService>> _mockLogger;
    private readonly VenueService _service;

    public VenueServiceTests()
    {
        _mockRepo = new Mock<IVenueRepository>();
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero));
        _mockLogger = new Mock<ILogger<VenueService>>();
        _service = new VenueService(_mockRepo.Object, _timeProvider, _mockLogger.Object);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task CreateVenueAsync_EmptyOrWhitespaceName_ThrowsArgumentException(string? name)
    {
        // Arrange
        var dto = new CreateVenueDto(name!, "Colombo 07", 500);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateVenueAsync(dto));
        _mockRepo.Verify(r => r.CreateVenueAsync(It.IsAny<Venue>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task CreateVenueAsync_EmptyOrWhitespaceAddress_ThrowsArgumentException(string? address)
    {
        // Arrange
        var dto = new CreateVenueDto("Nelum Pokuna", address!, 500);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateVenueAsync(dto));
        _mockRepo.Verify(r => r.CreateVenueAsync(It.IsAny<Venue>()), Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CreateVenueAsync_NonPositiveCapacity_ThrowsArgumentException(int capacity)
    {
        // Arrange
        var dto = new CreateVenueDto("Nelum Pokuna", "Colombo 07", capacity);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateVenueAsync(dto));
        _mockRepo.Verify(r => r.CreateVenueAsync(It.IsAny<Venue>()), Times.Never);
    }

    [Fact]
    public async Task CreateVenueAsync_ValidDto_TrimsFieldsAndSetsIdAndTimestampsFromTimeProvider()
    {
        // Arrange
        var dto = new CreateVenueDto("  Nelum Pokuna  ", "  Colombo 07  ", 500);
        _mockRepo.Setup(r => r.CreateVenueAsync(It.IsAny<Venue>())).ReturnsAsync((Venue v) => v);

        // Act
        var result = await _service.CreateVenueAsync(dto);

        // Assert
        Assert.Equal("Nelum Pokuna", result.Name);
        Assert.Equal("Colombo 07", result.Address);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal(_timeProvider.GetUtcNow().UtcDateTime, result.CreatedAt);
        Assert.Equal(_timeProvider.GetUtcNow().UtcDateTime, result.UpdatedAt);
    }

    [Fact]
    public async Task UpdateVenueAsync_UnknownId_ThrowsKeyNotFoundException()
    {
        // Arrange
        var id = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetVenueByIdAsync(id)).ReturnsAsync((Venue?)null);
        var dto = new UpdateVenueDto("New Name", "New Address", 200);

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.UpdateVenueAsync(id, dto));
        _mockRepo.Verify(r => r.UpdateVenueAsync(It.IsAny<Venue>()), Times.Never);
    }

    [Fact]
    public async Task UpdateVenueAsync_NonPositiveCapacity_ThrowsArgumentExceptionAndWritesNothing()
    {
        // Arrange
        var id = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetVenueByIdAsync(id)).ReturnsAsync(new Venue { Id = id, Name = "Old", Address = "Old Addr", Capacity = 100 });
        var dto = new UpdateVenueDto("New Name", "New Address", 0);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateVenueAsync(id, dto));
        _mockRepo.Verify(r => r.UpdateVenueAsync(It.IsAny<Venue>()), Times.Never);
    }

    [Fact]
    public async Task DeleteVenueAsync_UnknownId_ThrowsKeyNotFoundException()
    {
        // Arrange
        var id = Guid.NewGuid();
        _mockRepo.Setup(r => r.DeleteVenueIfUnusedAsync(id)).ReturnsAsync((VenueDeleteResult?)null);

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.DeleteVenueAsync(id));
    }

    [Fact]
    public async Task DeleteVenueAsync_VenueInUse_ThrowsInvalidOperationExceptionNamingCount()
    {
        // Arrange
        var id = Guid.NewGuid();
        _mockRepo.Setup(r => r.DeleteVenueIfUnusedAsync(id)).ReturnsAsync(new VenueDeleteResult(false, 3));

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteVenueAsync(id));
        Assert.Contains("3", ex.Message);
    }

    [Fact]
    public async Task DeleteVenueAsync_UnusedVenue_Succeeds()
    {
        // Arrange
        var id = Guid.NewGuid();
        _mockRepo.Setup(r => r.DeleteVenueIfUnusedAsync(id)).ReturnsAsync(new VenueDeleteResult(true, 0));

        // Act & Assert (no exception)
        await _service.DeleteVenueAsync(id);
    }
}
