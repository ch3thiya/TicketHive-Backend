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

// A null Id means "add this as a new category". A non-null Id must already
// belong to the show being updated (checked by SaveTicketCategoriesAsync).
public record UpdateTicketCategoryDto(
    Guid? Id,
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
    List<UpdateTicketCategoryDto>? Categories = null
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
    Task<List<EventWithShowsDto>> GetPublishedEventsAsync(string? search, string? category, DateOnly? fromDate, DateOnly? toDate, Guid? venueId);
    Task<EventWithShowsDto?> GetEventByIdAsync(Guid eventId);
    Task<EventWithShowsDto?> GetPublishedEventByIdAsync(Guid eventId);
    Task PublishEventAsync(Guid organizerId, Guid eventId);
    Task UpdateEventAsync(Guid organizerId, Guid eventId, UpdateEventDto dto);
    Task CancelEventAsync(Guid organizerId, Guid eventId);
    Task UpdateShowAsync(Guid organizerId, Guid showId, UpdateShowDto dto);
    Task CancelShowAsync(Guid organizerId, Guid showId);
}
