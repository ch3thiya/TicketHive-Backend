using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Catalog.Service.Db;
using Catalog.Service.Models;
using Catalog.Service.Services;

namespace Catalog.Service.Tests;

public class EventServiceTests
{
    private readonly Mock<IEventRepository> _mockRepo;
    private readonly Mock<ILogger<EventService>> _mockLogger;
    private readonly EventService _service;

    public EventServiceTests()
    {
        _mockRepo = new Mock<IEventRepository>();
        _mockLogger = new Mock<ILogger<EventService>>();
        _service = new EventService(_mockRepo.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task CreateEvent_ValidDto_CreatesDraftEventWithGuid()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var dto = new CreateEventDto(
            Name: "Neon Summer Concert",
            Description: "Live music event",
            Category: "Concert",
            EventDate: new DateOnly(2026, 9, 15),
            EventTime: new TimeOnly(18, 0),
            BannerUrl: "http://example.com/banner.jpg"
        );

        Event? savedEvt = null;
        _mockRepo.Setup(r => r.CreateEventAsync(It.IsAny<Event>()))
                 .Callback<Event>(e => savedEvt = e)
                 .ReturnsAsync((Event e) => e);

        // Act
        var result = await _service.CreateEventAsync(organizerId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Neon Summer Concert", result.Name);
        Assert.Equal("Draft", result.Status); // Must start as Draft
        Assert.Equal(organizerId, result.OrganizerId);
        _mockRepo.Verify(r => r.CreateEventAsync(It.IsAny<Event>()), Times.Once);
    }

    [Fact]
    public async Task CreateEvent_EmptyName_ThrowsArgumentException()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var dto = new CreateEventDto(
            Name: "",
            Description: "Description",
            Category: "General",
            EventDate: null,
            EventTime: null,
            BannerUrl: ""
        );

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateEventAsync(organizerId, dto));
        _mockRepo.Verify(r => r.CreateEventAsync(It.IsAny<Event>()), Times.Never);
    }

    [Fact]
    public async Task CreateShow_ValidShowAndCategories_ExecutesAtomicTransaction()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        var existingEvt = new Event
        {
            Id = eventId,
            OrganizerId = organizerId,
            Name = "Jazz Night",
            Status = "Draft"
        };

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId)).ReturnsAsync(existingEvt);

        var dto = new CreateShowRequestDto(
            ShowDate: new DateOnly(2026, 10, 1),
            ShowTime: new TimeOnly(20, 0),
            Categories: new List<CreateTicketCategoryDto>
            {
                new CreateTicketCategoryDto("General", 50.00m, 100),
                new CreateTicketCategoryDto("VIP", 150.00m, 20)
            }
        );

        _mockRepo.Setup(r => r.CreateShowWithCategoriesAsync(It.IsAny<Show>(), It.IsAny<List<TicketCategory>>()))
                 .ReturnsAsync((Show s, List<TicketCategory> c) => s);

        // Act
        var result = await _service.CreateShowAsync(organizerId, eventId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(eventId, result.EventId);
        Assert.Equal(2, result.TicketCategories.Count);
        _mockRepo.Verify(r => r.CreateShowWithCategoriesAsync(It.IsAny<Show>(), It.IsAny<List<TicketCategory>>()), Times.Once);
    }

    [Fact]
    public async Task CreateShow_InvalidCategoryPrice_ThrowsArgumentException()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId)).ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId });

        var dto = new CreateShowRequestDto(
            ShowDate: new DateOnly(2026, 10, 1),
            ShowTime: new TimeOnly(20, 0),
            Categories: new List<CreateTicketCategoryDto>
            {
                new CreateTicketCategoryDto("General", -10.00m, 100) // Negative price invalid
            }
        );

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateShowAsync(organizerId, eventId, dto));
        _mockRepo.Verify(r => r.CreateShowWithCategoriesAsync(It.IsAny<Show>(), It.IsAny<List<TicketCategory>>()), Times.Never);
    }

    [Fact]
    public async Task CreateShow_InvalidCategoryCapacity_ThrowsArgumentException()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId)).ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId });

        var dto = new CreateShowRequestDto(
            ShowDate: new DateOnly(2026, 10, 1),
            ShowTime: new TimeOnly(20, 0),
            Categories: new List<CreateTicketCategoryDto>
            {
                new CreateTicketCategoryDto("General", 50.00m, 0) // Zero capacity invalid
            }
        );

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateShowAsync(organizerId, eventId, dto));
        _mockRepo.Verify(r => r.CreateShowWithCategoriesAsync(It.IsAny<Show>(), It.IsAny<List<TicketCategory>>()), Times.Never);
    }

    [Fact]
    public async Task CreateShow_UnauthorizedOrganizer_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var callerId = Guid.NewGuid(); // Different organizer
        var eventId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId)).ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId });

        var dto = new CreateShowRequestDto(
            ShowDate: new DateOnly(2026, 10, 1),
            ShowTime: new TimeOnly(20, 0),
            Categories: new List<CreateTicketCategoryDto>
            {
                new CreateTicketCategoryDto("General", 50.00m, 50)
            }
        );

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.CreateShowAsync(callerId, eventId, dto));
        _mockRepo.Verify(r => r.CreateShowWithCategoriesAsync(It.IsAny<Show>(), It.IsAny<List<TicketCategory>>()), Times.Never);
    }

    [Fact]
    public async Task PublishEvent_ValidDraftEventWithShowAndCategory_UpdatesStatusToPublished()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var showId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId, Status = "Draft" });

        _mockRepo.Setup(r => r.GetShowsByEventIdAsync(eventId))
                 .ReturnsAsync(new List<Show> { new Show { Id = showId, EventId = eventId, Status = "Active" } });

        _mockRepo.Setup(r => r.GetTicketCategoriesByShowIdAsync(showId))
                 .ReturnsAsync(new List<TicketCategory> { new TicketCategory { Id = Guid.NewGuid(), ShowId = showId, Name = "GA", Price = 20, Capacity = 50 } });

        // Act
        await _service.PublishEventAsync(organizerId, eventId);

        // Assert
        _mockRepo.Verify(r => r.UpdateEventStatusAsync(eventId, "Published"), Times.Once);
    }

    [Fact]
    public async Task PublishEvent_NoShows_ThrowsInvalidOperationException()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId, Status = "Draft" });

        _mockRepo.Setup(r => r.GetShowsByEventIdAsync(eventId))
                 .ReturnsAsync(new List<Show>()); // No shows

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.PublishEventAsync(organizerId, eventId));
        _mockRepo.Verify(r => r.UpdateEventStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CancelEvent_AuthorizedOrganizer_UpdatesStatusToCancelled()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId, Status = "Published" });

        // Act
        await _service.CancelEventAsync(organizerId, eventId);

        // Assert
        _mockRepo.Verify(r => r.UpdateEventStatusAsync(eventId, "Cancelled"), Times.Once);
    }

    [Fact]
    public async Task CancelShow_AuthorizedOrganizer_UpdatesStatusToCancelled()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var showId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetShowByIdAsync(showId))
                 .ReturnsAsync(new Show { Id = showId, EventId = eventId, Status = "Active" });

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId, Status = "Published" });

        // Act
        await _service.CancelShowAsync(organizerId, showId);

        // Assert
        _mockRepo.Verify(r => r.UpdateShowStatusAsync(showId, "Cancelled"), Times.Once);
    }
}
