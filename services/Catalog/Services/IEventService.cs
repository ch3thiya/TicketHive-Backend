using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Catalog.Service.Models;

namespace Catalog.Service.Services;

public record CreateEventDto(
    string Name,
    string Description,
    string Category,
    DateOnly? EventDate,
    TimeOnly? EventTime,
    string BannerUrl,
    int? CancellationCutoffHours = null
);

public record CreateShowRequestDto(
    DateOnly ShowDate,
    TimeOnly ShowTime,
    List<CreateTicketCategoryDto> Categories,
    Guid? VenueId = null,
    DateTime? OnSaleAt = null,
    int? HighDemandThreshold = null,
    int? ReminderMinutesBefore = null
);

public record CreateTicketCategoryDto(
    string Name,
    decimal Price,
    int Capacity
);

public record UpdateEventDto(
    string Name,
    string Description,
    string Category,
    DateOnly? EventDate,
    TimeOnly? EventTime,
    string BannerUrl,
    int? CancellationCutoffHours = null
);

public record UpdateShowDto(
    DateOnly ShowDate,
    TimeOnly ShowTime,
    Guid? VenueId = null,
    DateTime? OnSaleAt = null,
    int? HighDemandThreshold = null,
    int? ReminderMinutesBefore = null,
    List<CreateTicketCategoryDto>? Categories = null
);

public record ShowDetailsDto(
    Guid Id,
    Guid EventId,
    DateOnly ShowDate,
    TimeOnly ShowTime,
    Guid? VenueId,
    DateTime? OnSaleAt,
    int? HighDemandThreshold,
    int? ReminderMinutesBefore,
    string Status,
    DateTime CreatedAt,
    List<TicketCategory> TicketCategories
);

public record EventWithShowsDto(
    Guid Id,
    Guid OrganizerId,
    string Name,
    string Description,
    string Category,
    DateOnly? EventDate,
    TimeOnly? EventTime,
    string BannerUrl,
    int? CancellationCutoffHours,
    string Status,
    DateTime CreatedAt,
    List<ShowDetailsDto> Shows
);

public interface IEventService
{
    Task<Event> CreateEventAsync(Guid organizerId, CreateEventDto dto);
    Task<ShowDetailsDto> CreateShowAsync(Guid organizerId, Guid eventId, CreateShowRequestDto dto);
    Task<List<EventWithShowsDto>> GetEventsByOrganizerIdAsync(Guid organizerId);
    Task<List<EventWithShowsDto>> GetAllPublishedEventsAsync();
    Task<EventWithShowsDto?> GetEventByIdAsync(Guid eventId);
    Task PublishEventAsync(Guid organizerId, Guid eventId);
    Task UpdateEventAsync(Guid organizerId, Guid eventId, UpdateEventDto dto);
    Task CancelEventAsync(Guid organizerId, Guid eventId);
    Task UpdateShowAsync(Guid organizerId, Guid showId, UpdateShowDto dto);
    Task CancelShowAsync(Guid organizerId, Guid showId);
}
