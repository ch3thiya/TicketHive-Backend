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
    private readonly Mock<IVenueService> _mockVenueService;
    private readonly Mock<ILogger<EventService>> _mockLogger;
    private readonly EventService _service;

    public EventServiceTests()
    {
        _mockRepo = new Mock<IEventRepository>();
        _mockVenueService = new Mock<IVenueService>();
        _mockVenueService.Setup(v => v.VenueExistsAsync(It.IsAny<Guid>())).ReturnsAsync(true);
        _mockLogger = new Mock<ILogger<EventService>>();
        _service = new EventService(_mockRepo.Object, _mockVenueService.Object, _mockLogger.Object);
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
            BannerUrl: "http://example.com/banner.jpg",
            CancellationCutoffHours: 48
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
        Assert.Equal(48, result.CancellationCutoffHours);
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
            BannerUrl: "",
            CancellationCutoffHours: null
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
        var venueId = Guid.NewGuid();

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
            },
            VenueId: venueId,
            OnSaleAt: new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
            HighDemandThreshold: 50,
            ReminderMinutesBefore: 120
        );

        _mockRepo.Setup(r => r.CreateShowWithCategoriesAsync(It.IsAny<Show>(), It.IsAny<List<TicketCategory>>()))
                 .ReturnsAsync((Show s, List<TicketCategory> c) => s);

        // Act
        var result = await _service.CreateShowAsync(organizerId, eventId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(eventId, result.EventId);
        Assert.Equal(venueId, result.VenueId);
        Assert.Equal(50, result.HighDemandThreshold);
        Assert.Equal(120, result.ReminderMinutesBefore);
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
    public async Task UpdateEvent_AuthorizedOrganizer_UpdatesFieldsSuccessfully()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        var existingEvt = new Event
        {
            Id = eventId,
            OrganizerId = organizerId,
            Name = "Old Name",
            Status = "Draft"
        };

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId)).ReturnsAsync(existingEvt);

        var updateDto = new UpdateEventDto(
            Name: "Updated Festival",
            Description: "Updated description",
            Category: "Festival",
            EventDate: new DateOnly(2026, 11, 20),
            EventTime: new TimeOnly(19, 30),
            BannerUrl: "http://example.com/new.jpg",
            CancellationCutoffHours: 72
        );

        // Act
        await _service.UpdateEventAsync(organizerId, eventId, updateDto);

        // Assert
        Assert.Equal("Updated Festival", existingEvt.Name);
        Assert.Equal(72, existingEvt.CancellationCutoffHours);
        _mockRepo.Verify(r => r.UpdateEventAsync(existingEvt), Times.Once);
    }

    [Fact]
    public async Task UpdateEvent_UnauthorizedOrganizer_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var otherOrganizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = ownerId, Name = "Rock Show" });

        var updateDto = new UpdateEventDto(
            Name: "Hacked Name",
            Description: "",
            Category: "",
            EventDate: null,
            EventTime: null,
            BannerUrl: "",
            CancellationCutoffHours: null
        );

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.UpdateEventAsync(otherOrganizerId, eventId, updateDto));
        _mockRepo.Verify(r => r.UpdateEventAsync(It.IsAny<Event>()), Times.Never);
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
    public async Task UpdateShow_AuthorizedOrganizer_UpdatesShowDetails()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var showId = Guid.NewGuid();
        var venueId = Guid.NewGuid();

        var existingShow = new Show
        {
            Id = showId,
            EventId = eventId,
            ShowDate = new DateOnly(2026, 9, 1),
            ShowTime = new TimeOnly(18, 0),
            Status = "Active"
        };

        _mockRepo.Setup(r => r.GetShowByIdAsync(showId)).ReturnsAsync(existingShow);
        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId)).ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId });

        var updateDto = new UpdateShowDto(
            ShowDate: new DateOnly(2026, 9, 2),
            ShowTime: new TimeOnly(20, 0),
            VenueId: venueId,
            OnSaleAt: new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc),
            HighDemandThreshold: 100,
            ReminderMinutesBefore: 60
        );

        // Act
        await _service.UpdateShowAsync(organizerId, showId, updateDto);

        // Assert
        Assert.Equal(new DateOnly(2026, 9, 2), existingShow.ShowDate);
        Assert.Equal(venueId, existingShow.VenueId);
        Assert.Equal(100, existingShow.HighDemandThreshold);
        _mockRepo.Verify(r => r.UpdateShowAsync(existingShow), Times.Once);
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

    [Fact]
    public async Task PublishEvent_ShowsWithNoTicketCategories_ThrowsInvalidOperationException()
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
                 .ReturnsAsync(new List<TicketCategory>()); // Empty categories

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.PublishEventAsync(organizerId, eventId));
        _mockRepo.Verify(r => r.UpdateEventStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task PublishEvent_UnauthorizedOrganizer_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = ownerId, Status = "Draft" });

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.PublishEventAsync(callerId, eventId));
        _mockRepo.Verify(r => r.UpdateEventStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CancelEvent_UnauthorizedOrganizer_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = ownerId, Status = "Published" });

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.CancelEventAsync(callerId, eventId));
        _mockRepo.Verify(r => r.UpdateEventStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateShow_UnauthorizedOrganizer_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var showId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetShowByIdAsync(showId))
                 .ReturnsAsync(new Show { Id = showId, EventId = eventId, Status = "Active" });

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = ownerId });

        var updateDto = new UpdateShowDto(
            ShowDate: new DateOnly(2026, 9, 2),
            ShowTime: new TimeOnly(20, 0)
        );

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.UpdateShowAsync(callerId, showId, updateDto));
        _mockRepo.Verify(r => r.UpdateShowAsync(It.IsAny<Show>()), Times.Never);
    }

    [Fact]
    public async Task UpdateShow_CategoryEmptyName_ThrowsArgumentException()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var showId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetShowByIdAsync(showId))
                 .ReturnsAsync(new Show { Id = showId, EventId = eventId, Status = "Active" });
        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId });

        var updateDto = new UpdateShowDto(
            ShowDate: new DateOnly(2026, 9, 2),
            ShowTime: new TimeOnly(20, 0),
            Categories: new List<UpdateTicketCategoryDto> { new(null, "", 50m, 100) }
        );

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateShowAsync(organizerId, showId, updateDto));
        _mockRepo.Verify(r => r.SaveTicketCategoriesAsync(It.IsAny<Guid>(), It.IsAny<List<TicketCategory>>()), Times.Never);
    }

    [Fact]
    public async Task UpdateShow_CategoryNegativePrice_ThrowsArgumentException()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var showId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetShowByIdAsync(showId))
                 .ReturnsAsync(new Show { Id = showId, EventId = eventId, Status = "Active" });
        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId });

        var updateDto = new UpdateShowDto(
            ShowDate: new DateOnly(2026, 9, 2),
            ShowTime: new TimeOnly(20, 0),
            Categories: new List<UpdateTicketCategoryDto> { new(null, "General", -10m, 100) }
        );

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateShowAsync(organizerId, showId, updateDto));
        _mockRepo.Verify(r => r.SaveTicketCategoriesAsync(It.IsAny<Guid>(), It.IsAny<List<TicketCategory>>()), Times.Never);
    }

    [Fact]
    public async Task UpdateShow_CategoryZeroCapacity_ThrowsArgumentException()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var showId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetShowByIdAsync(showId))
                 .ReturnsAsync(new Show { Id = showId, EventId = eventId, Status = "Active" });
        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId });

        var updateDto = new UpdateShowDto(
            ShowDate: new DateOnly(2026, 9, 2),
            ShowTime: new TimeOnly(20, 0),
            Categories: new List<UpdateTicketCategoryDto> { new(null, "General", 50m, 0) }
        );

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateShowAsync(organizerId, showId, updateDto));
        _mockRepo.Verify(r => r.SaveTicketCategoriesAsync(It.IsAny<Guid>(), It.IsAny<List<TicketCategory>>()), Times.Never);
    }

    [Fact]
    public async Task UpdateShow_CategoriesWithAndWithoutIds_MapsIdsCorrectly()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var showId = Guid.NewGuid();
        var existingCategoryId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetShowByIdAsync(showId))
                 .ReturnsAsync(new Show { Id = showId, EventId = eventId, Status = "Active" });
        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId });

        List<TicketCategory>? saved = null;
        _mockRepo.Setup(r => r.SaveTicketCategoriesAsync(showId, It.IsAny<List<TicketCategory>>()))
                 .Callback<Guid, List<TicketCategory>>((_, categories) => saved = categories)
                 .Returns(Task.CompletedTask);

        var updateDto = new UpdateShowDto(
            ShowDate: new DateOnly(2026, 9, 2),
            ShowTime: new TimeOnly(20, 0),
            Categories: new List<UpdateTicketCategoryDto>
            {
                new(existingCategoryId, "VIP", 100m, 20),
                new(null, "GA", 50m, 100)
            }
        );

        // Act
        await _service.UpdateShowAsync(organizerId, showId, updateDto);

        // Assert
        Assert.NotNull(saved);
        Assert.Equal(2, saved!.Count);
        Assert.Equal(existingCategoryId, saved[0].Id);
        Assert.Equal(Guid.Empty, saved[1].Id);
    }

    [Fact]
    public async Task UpdateShow_RepositoryRejectsCategoryId_PropagatesArgumentException()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var showId = Guid.NewGuid();
        var foreignCategoryId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetShowByIdAsync(showId))
                 .ReturnsAsync(new Show { Id = showId, EventId = eventId, Status = "Active" });
        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId });
        _mockRepo.Setup(r => r.SaveTicketCategoriesAsync(showId, It.IsAny<List<TicketCategory>>()))
                 .ThrowsAsync(new ArgumentException($"Ticket category '{foreignCategoryId}' does not belong to this show."));

        var updateDto = new UpdateShowDto(
            ShowDate: new DateOnly(2026, 9, 2),
            ShowTime: new TimeOnly(20, 0),
            Categories: new List<UpdateTicketCategoryDto> { new(foreignCategoryId, "VIP", 100m, 20) }
        );

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateShowAsync(organizerId, showId, updateDto));
    }

    [Fact]
    public async Task CancelShow_UnauthorizedOrganizer_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var showId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetShowByIdAsync(showId))
                 .ReturnsAsync(new Show { Id = showId, EventId = eventId, Status = "Active" });

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = ownerId });

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.CancelShowAsync(callerId, showId));
        _mockRepo.Verify(r => r.UpdateShowStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CreateShow_EventNotFound_ThrowsKeyNotFoundException()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId)).ReturnsAsync((Event?)null);

        var dto = new CreateShowRequestDto(
            ShowDate: new DateOnly(2026, 10, 1),
            ShowTime: new TimeOnly(20, 0),
            Categories: new List<CreateTicketCategoryDto> { new CreateTicketCategoryDto("GA", 50, 100) }
        );

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.CreateShowAsync(organizerId, eventId, dto));
        _mockRepo.Verify(r => r.CreateShowWithCategoriesAsync(It.IsAny<Show>(), It.IsAny<List<TicketCategory>>()), Times.Never);
    }

    [Fact]
    public async Task CreateShow_ZeroCategories_ThrowsArgumentException()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId });

        var dto = new CreateShowRequestDto(
            ShowDate: new DateOnly(2026, 10, 1),
            ShowTime: new TimeOnly(20, 0),
            Categories: new List<CreateTicketCategoryDto>() // Empty categories
        );

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateShowAsync(organizerId, eventId, dto));
        _mockRepo.Verify(r => r.CreateShowWithCategoriesAsync(It.IsAny<Show>(), It.IsAny<List<TicketCategory>>()), Times.Never);
    }

    [Fact]
    public async Task CreateShow_EmptyCategoryName_ThrowsArgumentException()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId });

        var dto = new CreateShowRequestDto(
            ShowDate: new DateOnly(2026, 10, 1),
            ShowTime: new TimeOnly(20, 0),
            Categories: new List<CreateTicketCategoryDto>
            {
                new CreateTicketCategoryDto("", 50.00m, 100) // Empty name
            }
        );

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateShowAsync(organizerId, eventId, dto));
        _mockRepo.Verify(r => r.CreateShowWithCategoriesAsync(It.IsAny<Show>(), It.IsAny<List<TicketCategory>>()), Times.Never);
    }

    [Fact]
    public async Task GetPublishedEvents_ReturnsOnlyPublishedEventsWithShowsAndCategories()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        var showId = Guid.NewGuid();
        var publishedEvent = new Event
        {
            Id = eventId,
            OrganizerId = Guid.NewGuid(),
            Name = "Published Concert",
            Category = "Concerts",
            Status = "Published",
            CreatedAt = DateTime.UtcNow
        };

        _mockRepo.Setup(r => r.GetPublishedEventsAsync(null, null, null, null, null))
                 .ReturnsAsync(new List<Event> { publishedEvent });

        _mockRepo.Setup(r => r.GetShowsByEventIdAsync(eventId))
                 .ReturnsAsync(new List<Show>
                 {
                     new Show { Id = showId, EventId = eventId, ShowDate = new DateOnly(2026, 10, 1), ShowTime = new TimeOnly(19, 30), Status = "Active" }
                 });

        _mockRepo.Setup(r => r.GetTicketCategoriesByShowIdAsync(showId))
                 .ReturnsAsync(new List<TicketCategory>
                 {
                     new TicketCategory { Id = Guid.NewGuid(), ShowId = showId, Name = "VIP", Price = 100m, Capacity = 50 }
                 });

        // Act
        var result = await _service.GetPublishedEventsAsync(null, null, null, null, null);

        // Assert
        Assert.Single(result);
        Assert.Equal(eventId, result[0].Id);
        Assert.Equal("Published Concert", result[0].Name);
        Assert.Equal("Published", result[0].Status);
        Assert.Single(result[0].Shows);
        Assert.Single(result[0].Shows[0].TicketCategories);
        _mockRepo.Verify(r => r.GetPublishedEventsAsync(null, null, null, null, null), Times.Once);
    }

    [Fact]
    public async Task GetPublishedEvents_WithKeywordSearch_PassesSearchParamToRepository()
    {
        // Arrange
        _mockRepo.Setup(r => r.GetPublishedEventsAsync("Rock", null, null, null, null))
                 .ReturnsAsync(new List<Event>());

        // Act
        var result = await _service.GetPublishedEventsAsync("Rock", null, null, null, null);

        // Assert
        Assert.Empty(result);
        _mockRepo.Verify(r => r.GetPublishedEventsAsync("Rock", null, null, null, null), Times.Once);
    }

    [Fact]
    public async Task GetPublishedEvents_WithCategoryFilter_PassesCategoryParamToRepository()
    {
        // Arrange
        _mockRepo.Setup(r => r.GetPublishedEventsAsync(null, "Movies", null, null, null))
                 .ReturnsAsync(new List<Event>());

        // Act
        var result = await _service.GetPublishedEventsAsync(null, "Movies", null, null, null);

        // Assert
        Assert.Empty(result);
        _mockRepo.Verify(r => r.GetPublishedEventsAsync(null, "Movies", null, null, null), Times.Once);
    }

    [Fact]
    public async Task GetPublishedEvents_WithDateRange_PassesDateRangeToRepository()
    {
        // Arrange
        var fromDate = new DateOnly(2026, 9, 1);
        var toDate = new DateOnly(2026, 9, 30);
        _mockRepo.Setup(r => r.GetPublishedEventsAsync(null, null, fromDate, toDate, null))
                 .ReturnsAsync(new List<Event>());

        // Act
        var result = await _service.GetPublishedEventsAsync(null, null, fromDate, toDate, null);

        // Assert
        Assert.Empty(result);
        _mockRepo.Verify(r => r.GetPublishedEventsAsync(null, null, fromDate, toDate, null), Times.Once);
    }

    [Fact]
    public async Task GetPublishedEvents_WithVenueFilter_PassesVenueIdToRepository()
    {
        // Arrange
        var venueId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetPublishedEventsAsync(null, null, null, null, venueId))
                 .ReturnsAsync(new List<Event>());

        // Act
        var result = await _service.GetPublishedEventsAsync(null, null, null, null, venueId);

        // Assert
        Assert.Empty(result);
        _mockRepo.Verify(r => r.GetPublishedEventsAsync(null, null, null, null, venueId), Times.Once);
    }

    [Fact]
    public async Task GetPublishedEvents_WithCombinedFilters_PassesAllParamsToRepository()
    {
        // Arrange
        var fromDate = new DateOnly(2026, 9, 1);
        var toDate = new DateOnly(2026, 9, 30);
        var venueId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetPublishedEventsAsync("Jazz", "Concerts", fromDate, toDate, venueId))
                 .ReturnsAsync(new List<Event>());

        // Act
        var result = await _service.GetPublishedEventsAsync("Jazz", "Concerts", fromDate, toDate, venueId);

        // Assert
        Assert.Empty(result);
        _mockRepo.Verify(r => r.GetPublishedEventsAsync("Jazz", "Concerts", fromDate, toDate, venueId), Times.Once);
    }

    [Fact]
    public async Task GetPublishedEvents_NoMatchingEvents_ReturnsEmptyList()
    {
        // Arrange
        _mockRepo.Setup(r => r.GetPublishedEventsAsync("NonExistent", null, null, null, null))
                 .ReturnsAsync(new List<Event>());

        // Act
        var result = await _service.GetPublishedEventsAsync("NonExistent", null, null, null, null);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPublishedEvents_InvalidDateRange_ThrowsArgumentException()
    {
        // Arrange
        var fromDate = new DateOnly(2026, 10, 1);
        var toDate = new DateOnly(2026, 9, 1); // fromDate > toDate

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.GetPublishedEventsAsync(null, null, fromDate, toDate, null));

        Assert.Contains("fromDate", ex.Message);
        _mockRepo.Verify(r => r.GetPublishedEventsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateOnly?>(), It.IsAny<DateOnly?>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task GetPublishedEventById_PublishedEvent_ReturnsEventDetails()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        var showId = Guid.NewGuid();
        var publishedEvent = new Event
        {
            Id = eventId,
            OrganizerId = Guid.NewGuid(),
            Name = "Festival 2026",
            Category = "Festival",
            Status = "Published",
            CreatedAt = DateTime.UtcNow
        };

        _mockRepo.Setup(r => r.GetPublishedEventByIdAsync(eventId))
                 .ReturnsAsync(publishedEvent);

        _mockRepo.Setup(r => r.GetShowsByEventIdAsync(eventId))
                 .ReturnsAsync(new List<Show>
                 {
                     new Show { Id = showId, EventId = eventId, ShowDate = new DateOnly(2026, 11, 1), ShowTime = new TimeOnly(18, 0), Status = "Active" }
                 });

        _mockRepo.Setup(r => r.GetTicketCategoriesByShowIdAsync(showId))
                 .ReturnsAsync(new List<TicketCategory>
                 {
                     new TicketCategory { Id = Guid.NewGuid(), ShowId = showId, Name = "GA", Price = 45m, Capacity = 200 }
                 });

        // Act
        var result = await _service.GetPublishedEventByIdAsync(eventId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(eventId, result.Id);
        Assert.Equal("Festival 2026", result.Name);
        Assert.Equal("Published", result.Status);
        Assert.Single(result.Shows);
        Assert.Single(result.Shows[0].TicketCategories);
        _mockRepo.Verify(r => r.GetPublishedEventByIdAsync(eventId), Times.Once);
    }

    [Fact]
    public async Task GetPublishedEventById_DraftEvent_ReturnsNull()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        // Repository returns null because GetPublishedEventByIdAsync has `status = 'Published'` filter in DB
        _mockRepo.Setup(r => r.GetPublishedEventByIdAsync(eventId))
                 .ReturnsAsync((Event?)null);

        // Act
        var result = await _service.GetPublishedEventByIdAsync(eventId);

        // Assert
        Assert.Null(result);
        _mockRepo.Verify(r => r.GetPublishedEventByIdAsync(eventId), Times.Once);
    }

    [Fact]
    public async Task GetPublishedEventById_CancelledEvent_ReturnsNull()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        // Repository returns null for cancelled events when querying published-only
        _mockRepo.Setup(r => r.GetPublishedEventByIdAsync(eventId))
                 .ReturnsAsync((Event?)null);

        // Act
        var result = await _service.GetPublishedEventByIdAsync(eventId);

        // Assert
        Assert.Null(result);
        _mockRepo.Verify(r => r.GetPublishedEventByIdAsync(eventId), Times.Once);
    }

    [Fact]
    public async Task GetPublishedEventById_NonExistentEvent_ReturnsNull()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        _mockRepo.Setup(r => r.GetPublishedEventByIdAsync(eventId))
                 .ReturnsAsync((Event?)null);

        // Act
        var result = await _service.GetPublishedEventByIdAsync(eventId);

        // Assert
        Assert.Null(result);
        _mockRepo.Verify(r => r.GetPublishedEventByIdAsync(eventId), Times.Once);
    }

    [Fact]
    public async Task PublishEvent_CancelledEvent_ThrowsInvalidOperationException()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId, Status = "Cancelled" });

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.PublishEventAsync(organizerId, eventId));
        _mockRepo.Verify(r => r.GetShowsByEventIdAsync(It.IsAny<Guid>()), Times.Never);
        _mockRepo.Verify(r => r.UpdateEventStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task PublishEvent_AlreadyPublishedEvent_ThrowsInvalidOperationException()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId, Status = "Published" });

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.PublishEventAsync(organizerId, eventId));
        _mockRepo.Verify(r => r.UpdateEventStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CancelEvent_AlreadyCancelledEvent_ThrowsInvalidOperationException()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId, Status = "Cancelled" });

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CancelEventAsync(organizerId, eventId));
        _mockRepo.Verify(r => r.UpdateEventStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CancelEvent_DraftEvent_UpdatesStatusToCancelled()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId, Status = "Draft" });

        // Act
        await _service.CancelEventAsync(organizerId, eventId);

        // Assert
        _mockRepo.Verify(r => r.UpdateEventStatusAsync(eventId, "Cancelled"), Times.Once);
    }

    [Fact]
    public async Task CancelShow_AlreadyCancelledShow_ThrowsInvalidOperationException()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var showId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetShowByIdAsync(showId))
                 .ReturnsAsync(new Show { Id = showId, EventId = eventId, Status = "Cancelled" });

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId, Status = "Published" });

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CancelShowAsync(organizerId, showId));
        _mockRepo.Verify(r => r.UpdateShowStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateEvent_CancelledEvent_ThrowsInvalidOperationException()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId, Name = "Old Name", Status = "Cancelled" });

        var updateDto = new UpdateEventDto(
            Name: "Updated Name",
            Description: "",
            Category: "",
            EventDate: null,
            EventTime: null,
            BannerUrl: "",
            CancellationCutoffHours: null
        );

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdateEventAsync(organizerId, eventId, updateDto));
        _mockRepo.Verify(r => r.UpdateEventAsync(It.IsAny<Event>()), Times.Never);
    }

    [Fact]
    public async Task UpdateShow_CancelledShow_ThrowsInvalidOperationException()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var showId = Guid.NewGuid();

        _mockRepo.Setup(r => r.GetShowByIdAsync(showId))
                 .ReturnsAsync(new Show { Id = showId, EventId = eventId, Status = "Cancelled" });

        _mockRepo.Setup(r => r.GetEventByIdAsync(eventId))
                 .ReturnsAsync(new Event { Id = eventId, OrganizerId = organizerId });

        var updateDto = new UpdateShowDto(
            ShowDate: new DateOnly(2026, 9, 2),
            ShowTime: new TimeOnly(20, 0)
        );

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdateShowAsync(organizerId, showId, updateDto));
        _mockRepo.Verify(r => r.UpdateShowAsync(It.IsAny<Show>()), Times.Never);
    }
}
