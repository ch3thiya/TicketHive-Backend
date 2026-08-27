using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Catalog.Service.Db;
using Catalog.Service.Models;

namespace Catalog.Service.Services;

public class EventService : IEventService
{
    private readonly IEventRepository _repository;
    private readonly ILogger<EventService> _logger;

    public EventService(IEventRepository repository, ILogger<EventService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<Event> CreateEventAsync(Guid organizerId, CreateEventDto dto)
    {
        if (organizerId == Guid.Empty)
        {
            throw new ArgumentException("Organizer ID is required.", nameof(organizerId));
        }

        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new ArgumentException("Event Name is required.", nameof(dto.Name));
        }

        var evt = new Event
        {
            Id = Guid.NewGuid(),
            OrganizerId = organizerId,
            Name = dto.Name.Trim(),
            Description = dto.Description?.Trim() ?? string.Empty,
            Category = string.IsNullOrWhiteSpace(dto.Category) ? "General" : dto.Category.Trim(),
            EventDate = dto.EventDate,
            EventTime = dto.EventTime,
            BannerUrl = dto.BannerUrl?.Trim() ?? string.Empty,
            Status = "Draft", // Always starts as Draft
            CreatedAt = DateTime.UtcNow
        };

        _logger.LogInformation("Creating new Draft Event '{Name}' for Organizer {OrganizerId}", evt.Name, organizerId);
        return await _repository.CreateEventAsync(evt);
    }

    public async Task<ShowDetailsDto> CreateShowAsync(Guid organizerId, Guid eventId, CreateShowRequestDto dto)
    {
        var evt = await _repository.GetEventByIdAsync(eventId);
        if (evt == null)
        {
            throw new KeyNotFoundException($"Event with ID '{eventId}' was not found.");
        }

        if (evt.OrganizerId != organizerId)
        {
            throw new UnauthorizedAccessException("You are not authorized to create a show for this event.");
        }

        if (dto.Categories == null || dto.Categories.Count == 0)
        {
            throw new ArgumentException("At least one ticket category is required to create a show.");
        }

        var domainCategories = new List<TicketCategory>();
        foreach (var catDto in dto.Categories)
        {
            if (string.IsNullOrWhiteSpace(catDto.Name))
            {
                throw new ArgumentException("Ticket category name cannot be empty.");
            }

            if (catDto.Price < 0)
            {
                throw new ArgumentException($"Ticket category price must be non-negative. Invalid price: {catDto.Price}");
            }

            if (catDto.Capacity <= 0)
            {
                throw new ArgumentException($"Ticket category capacity must be positive. Invalid capacity: {catDto.Capacity}");
            }

            domainCategories.Add(new TicketCategory
            {
                Id = Guid.NewGuid(),
                Name = catDto.Name.Trim(),
                Price = catDto.Price,
                Capacity = catDto.Capacity
            });
        }

        var show = new Show
        {
            Id = Guid.NewGuid(),
            EventId = eventId,
            ShowDate = dto.ShowDate,
            ShowTime = dto.ShowTime,
            Status = "Active"
        };

        _logger.LogInformation("Executing atomic Show & Ticket Categories creation for Event {EventId}", eventId);
        await _repository.CreateShowWithCategoriesAsync(show, domainCategories);

        return new ShowDetailsDto(
            show.Id,
            show.EventId,
            show.ShowDate,
            show.ShowTime,
            show.Status,
            show.CreatedAt,
            domainCategories
        );
    }

    public async Task<List<EventWithShowsDto>> GetEventsByOrganizerIdAsync(Guid organizerId)
    {
        var events = await _repository.GetEventsByOrganizerIdAsync(organizerId);
        var result = new List<EventWithShowsDto>();

        foreach (var evt in events)
        {
            var shows = await _repository.GetShowsByEventIdAsync(evt.Id);
            var showDtos = new List<ShowDetailsDto>();

            foreach (var s in shows)
            {
                var categories = await _repository.GetTicketCategoriesByShowIdAsync(s.Id);
                showDtos.Add(new ShowDetailsDto(s.Id, s.EventId, s.ShowDate, s.ShowTime, s.Status, s.CreatedAt, categories));
            }

            result.Add(new EventWithShowsDto(
                evt.Id,
                evt.OrganizerId,
                evt.Name,
                evt.Description,
                evt.Category,
                evt.EventDate,
                evt.EventTime,
                evt.BannerUrl,
                evt.Status,
                evt.CreatedAt,
                showDtos
            ));
        }

        return result;
    }

    public async Task<List<EventWithShowsDto>> GetAllPublishedEventsAsync()
    {
        var events = await _repository.GetAllPublishedEventsAsync();
        var result = new List<EventWithShowsDto>();

        foreach (var evt in events)
        {
            var shows = await _repository.GetShowsByEventIdAsync(evt.Id);
            var showDtos = new List<ShowDetailsDto>();

            foreach (var s in shows)
            {
                var categories = await _repository.GetTicketCategoriesByShowIdAsync(s.Id);
                showDtos.Add(new ShowDetailsDto(s.Id, s.EventId, s.ShowDate, s.ShowTime, s.Status, s.CreatedAt, categories));
            }

            result.Add(new EventWithShowsDto(
                evt.Id,
                evt.OrganizerId,
                evt.Name,
                evt.Description,
                evt.Category,
                evt.EventDate,
                evt.EventTime,
                evt.BannerUrl,
                evt.Status,
                evt.CreatedAt,
                showDtos
            ));
        }

        return result;
    }

    public async Task<EventWithShowsDto?> GetEventByIdAsync(Guid eventId)
    {
        var evt = await _repository.GetEventByIdAsync(eventId);
        if (evt == null)
        {
            return null;
        }

        var shows = await _repository.GetShowsByEventIdAsync(evt.Id);
        var showDtos = new List<ShowDetailsDto>();

        foreach (var s in shows)
        {
            var categories = await _repository.GetTicketCategoriesByShowIdAsync(s.Id);
            showDtos.Add(new ShowDetailsDto(s.Id, s.EventId, s.ShowDate, s.ShowTime, s.Status, s.CreatedAt, categories));
        }

        return new EventWithShowsDto(
            evt.Id,
            evt.OrganizerId,
            evt.Name,
            evt.Description,
            evt.Category,
            evt.EventDate,
            evt.EventTime,
            evt.BannerUrl,
            evt.Status,
            evt.CreatedAt,
            showDtos
        );
    }

    public async Task PublishEventAsync(Guid organizerId, Guid eventId)
    {
        var evt = await _repository.GetEventByIdAsync(eventId);
        if (evt == null)
        {
            throw new KeyNotFoundException($"Event with ID '{eventId}' was not found.");
        }

        if (evt.OrganizerId != organizerId)
        {
            throw new UnauthorizedAccessException("You are not authorized to publish this event.");
        }

        var shows = await _repository.GetShowsByEventIdAsync(eventId);
        if (shows == null || shows.Count == 0)
        {
            throw new InvalidOperationException("An event must have at least one active show before it can be published.");
        }

        bool hasTicketCategories = false;
        foreach (var show in shows)
        {
            var categories = await _repository.GetTicketCategoriesByShowIdAsync(show.Id);
            if (categories != null && categories.Count > 0)
            {
                hasTicketCategories = true;
                break;
            }
        }

        if (!hasTicketCategories)
        {
            throw new InvalidOperationException("An event show must have at least one ticket category defined before publishing.");
        }

        _logger.LogInformation("Publishing Event {EventId} for Organizer {OrganizerId}", eventId, organizerId);
        await _repository.UpdateEventStatusAsync(eventId, "Published");
    }

    public async Task UpdateEventAsync(Guid organizerId, Guid eventId, UpdateEventDto dto)
    {
        var evt = await _repository.GetEventByIdAsync(eventId);
        if (evt == null)
        {
            throw new KeyNotFoundException($"Event with ID '{eventId}' was not found.");
        }

        if (evt.OrganizerId != organizerId)
        {
            throw new UnauthorizedAccessException("You are not authorized to update this event.");
        }

        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new ArgumentException("Event Name is required.", nameof(dto.Name));
        }

        evt.Name = dto.Name.Trim();
        evt.Description = dto.Description?.Trim() ?? string.Empty;
        evt.Category = string.IsNullOrWhiteSpace(dto.Category) ? "General" : dto.Category.Trim();
        evt.EventDate = dto.EventDate;
        evt.EventTime = dto.EventTime;
        evt.BannerUrl = dto.BannerUrl?.Trim() ?? string.Empty;

        await _repository.UpdateEventAsync(evt);
    }

    public async Task CancelEventAsync(Guid organizerId, Guid eventId)
    {
        var evt = await _repository.GetEventByIdAsync(eventId);
        if (evt == null)
        {
            throw new KeyNotFoundException($"Event with ID '{eventId}' was not found.");
        }

        if (evt.OrganizerId != organizerId)
        {
            throw new UnauthorizedAccessException("You are not authorized to cancel this event.");
        }

        _logger.LogInformation("Cancelling Event {EventId} for Organizer {OrganizerId}", eventId, organizerId);
        await _repository.UpdateEventStatusAsync(eventId, "Cancelled");
    }

    public async Task UpdateShowAsync(Guid organizerId, Guid showId, UpdateShowDto dto)
    {
        var show = await _repository.GetShowByIdAsync(showId);
        if (show == null)
        {
            throw new KeyNotFoundException($"Show with ID '{showId}' was not found.");
        }

        var evt = await _repository.GetEventByIdAsync(show.EventId);
        if (evt == null || evt.OrganizerId != organizerId)
        {
            throw new UnauthorizedAccessException("You are not authorized to update this show.");
        }

        show.ShowDate = dto.ShowDate;
        show.ShowTime = dto.ShowTime;

        await _repository.UpdateShowAsync(show);
    }

    public async Task CancelShowAsync(Guid organizerId, Guid showId)
    {
        var show = await _repository.GetShowByIdAsync(showId);
        if (show == null)
        {
            throw new KeyNotFoundException($"Show with ID '{showId}' was not found.");
        }

        var evt = await _repository.GetEventByIdAsync(show.EventId);
        if (evt == null || evt.OrganizerId != organizerId)
        {
            throw new UnauthorizedAccessException("You are not authorized to cancel this show.");
        }

        _logger.LogInformation("Cancelling Show {ShowId} for Organizer {OrganizerId}", showId, organizerId);
        await _repository.UpdateShowStatusAsync(showId, "Cancelled");
    }
}
