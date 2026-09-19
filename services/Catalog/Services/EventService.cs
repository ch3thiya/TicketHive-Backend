using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Catalog.Service.Clients;
using Catalog.Service.Db;
using Catalog.Service.Models;

namespace Catalog.Service.Services;

public class EventService : IEventService
{
    // ADR-004: money is numeric plus a currency code; this platform only
    // ever prices in LKR, so there is no per-category currency to carry.
    private const string Currency = "LKR";
    private const string AllocationMode = "GA";

    private readonly IEventRepository _repository;
    private readonly IVenueService _venueService;
    private readonly IInventoryClient _inventoryClient;
    private readonly IOptions<PublishDefaultsOptions> _publishDefaults;
    private readonly ILogger<EventService> _logger;
    private readonly TimeProvider _timeProvider;

    public EventService(
        IEventRepository repository,
        IVenueService venueService,
        IInventoryClient inventoryClient,
        IOptions<PublishDefaultsOptions> publishDefaults,
        ILogger<EventService> logger,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _venueService = venueService;
        _inventoryClient = inventoryClient;
        _publishDefaults = publishDefaults;
        _logger = logger;
        _timeProvider = timeProvider;
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
            CancellationCutoffHours = dto.CancellationCutoffHours,
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime
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

        if (dto.VenueId.HasValue && !await _venueService.VenueExistsAsync(dto.VenueId.Value))
        {
            throw new ArgumentException($"Venue '{dto.VenueId}' does not exist.", nameof(dto.VenueId));
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
            VenueId = dto.VenueId,
            OnSaleAt = dto.OnSaleAt,
            HighDemandThreshold = dto.HighDemandThreshold,
            ReminderMinutesBefore = dto.ReminderMinutesBefore,
            Status = "Active"
        };

        _logger.LogInformation("Executing atomic Show & Ticket Categories creation for Event {EventId}", eventId);
        await _repository.CreateShowWithCategoriesAsync(show, domainCategories);

        return new ShowDetailsDto(
            show.Id,
            show.EventId,
            show.ShowDate,
            show.ShowTime,
            show.VenueId,
            show.OnSaleAt,
            show.HighDemandThreshold,
            show.ReminderMinutesBefore,
            show.Status,
            show.CreatedAt,
            domainCategories
        );
    }

    public async Task<List<EventWithShowsDto>> GetEventsByOrganizerIdAsync(Guid organizerId)
    {
        var events = await _repository.GetEventsByOrganizerIdAsync(organizerId);
        return await AssembleEventsWithShowsAsync(events);
    }

    public async Task<List<EventWithShowsDto>> GetAllPublishedEventsAsync()
    {
        var events = await _repository.GetAllPublishedEventsAsync();
        return await AssembleEventsWithShowsAsync(events);
    }

    public async Task<List<EventWithShowsDto>> GetPublishedEventsAsync(string? search, string? category, DateOnly? fromDate, DateOnly? toDate, Guid? venueId)
    {
        if (fromDate.HasValue && toDate.HasValue && fromDate.Value > toDate.Value)
        {
            throw new ArgumentException("'fromDate' cannot be after 'toDate'.");
        }

        var events = await _repository.GetPublishedEventsAsync(search, category, fromDate, toDate, venueId);
        return await AssembleEventsWithShowsAsync(events);
    }

    // Assembles a list of events with their shows and ticket categories using
    // two batched queries (all shows for these events, then all categories
    // for those shows) instead of one query per event and per show, so a
    // listing costs a constant number of round trips regardless of size.
    private async Task<List<EventWithShowsDto>> AssembleEventsWithShowsAsync(List<Event> events)
    {
        if (events.Count == 0)
        {
            return new List<EventWithShowsDto>();
        }

        var eventIds = events.Select(e => e.Id).ToArray();
        var showsByEventId = await _repository.GetShowsByEventIdsAsync(eventIds);

        var showIds = showsByEventId.Values.SelectMany(shows => shows).Select(s => s.Id).ToArray();
        var categoriesByShowId = await _repository.GetTicketCategoriesByShowIdsAsync(showIds);

        var result = new List<EventWithShowsDto>(events.Count);
        foreach (var evt in events)
        {
            var shows = showsByEventId.GetValueOrDefault(evt.Id, new List<Show>());
            var showDtos = shows
                .Select(s => new ShowDetailsDto(
                    s.Id,
                    s.EventId,
                    s.ShowDate,
                    s.ShowTime,
                    s.VenueId,
                    s.OnSaleAt,
                    s.HighDemandThreshold,
                    s.ReminderMinutesBefore,
                    s.Status,
                    s.CreatedAt,
                    categoriesByShowId.GetValueOrDefault(s.Id, new List<TicketCategory>())))
                .ToList();

            result.Add(new EventWithShowsDto(
                evt.Id,
                evt.OrganizerId,
                evt.Name,
                evt.Description,
                evt.Category,
                evt.EventDate,
                evt.EventTime,
                evt.BannerUrl,
                evt.CancellationCutoffHours,
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
            showDtos.Add(new ShowDetailsDto(s.Id, s.EventId, s.ShowDate, s.ShowTime, s.VenueId, s.OnSaleAt, s.HighDemandThreshold, s.ReminderMinutesBefore, s.Status, s.CreatedAt, categories));
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
            evt.CancellationCutoffHours,
            evt.Status,
            evt.CreatedAt,
            showDtos
        );
    }

    public async Task<EventWithShowsDto?> GetPublishedEventByIdAsync(Guid eventId)
    {
        var evt = await _repository.GetPublishedEventByIdAsync(eventId);
        if (evt == null)
        {
            return null;
        }

        var shows = await _repository.GetShowsByEventIdAsync(evt.Id);
        var showDtos = new List<ShowDetailsDto>();

        foreach (var s in shows)
        {
            var categories = await _repository.GetTicketCategoriesByShowIdAsync(s.Id);
            showDtos.Add(new ShowDetailsDto(s.Id, s.EventId, s.ShowDate, s.ShowTime, s.VenueId, s.OnSaleAt, s.HighDemandThreshold, s.ReminderMinutesBefore, s.Status, s.CreatedAt, categories));
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
            evt.CancellationCutoffHours,
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

        if (!EventStatusTransitions.CanTransition(evt.Status, "Published", out var transitionReason))
        {
            throw new InvalidOperationException(transitionReason);
        }

        var shows = await _repository.GetShowsByEventIdAsync(eventId);
        if (shows == null || shows.Count == 0)
        {
            throw new InvalidOperationException("An event must have at least one active show before it can be published.");
        }

        var showsWithCategories = new List<(Show Show, List<TicketCategory> Categories)>();
        foreach (var show in shows)
        {
            var categories = await _repository.GetTicketCategoriesByShowIdAsync(show.Id);
            if (categories == null || categories.Count == 0)
            {
                throw new InvalidOperationException("An event show must have at least one ticket category defined before publishing.");
            }

            showsWithCategories.Add((show, categories));
        }

        // The remote, idempotent step runs before the local commit: a crash
        // between them leaves the event in Draft with stock already
        // initialized, so retrying the publish is harmless (SCRUM-8).
        var defaults = _publishDefaults.Value;
        foreach (var (show, categories) in showsWithCategories)
        {
            var request = new InitializeShowStockRequest(
                OrganizerId: organizerId,
                OnSaleAt: null, // until S2-05 introduces sales rules
                MaxPerCustomer: defaults.MaxPerCustomer,
                HoldMinutes: defaults.HoldMinutes,
                HighDemand: show.HighDemandThreshold.HasValue && show.HighDemandThreshold.Value > 0,
                Categories: categories.Select(c => new InitializeShowStockCategory(
                    CategoryId: c.Id,
                    Capacity: c.Capacity,
                    UnitPrice: c.Price,
                    Currency: Currency,
                    AllocationMode: AllocationMode
                )).ToList(),
                HighDemandThreshold: show.HighDemandThreshold
            );

            await _inventoryClient.InitializeShowStockAsync(show.Id, request);
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

        if (!EventStatusTransitions.CanEdit(evt.Status, out var editReason))
        {
            throw new InvalidOperationException(editReason);
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
        evt.CancellationCutoffHours = dto.CancellationCutoffHours;

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

        if (!EventStatusTransitions.CanTransition(evt.Status, "Cancelled", out var transitionReason))
        {
            throw new InvalidOperationException(transitionReason);
        }

        _logger.LogInformation("Cancelling Event {EventId} for Organizer {OrganizerId}", eventId, organizerId);
        await _repository.UpdateEventStatusAsync(eventId, "Cancelled");
    }

    public async Task DeleteEventAsync(Guid organizerId, Guid eventId)
    {
        var evt = await _repository.GetEventByIdAsync(eventId);
        if (evt == null)
        {
            throw new KeyNotFoundException($"Event with ID '{eventId}' was not found.");
        }

        if (evt.OrganizerId != organizerId)
        {
            throw new UnauthorizedAccessException("You are not authorized to delete this event.");
        }

        if (evt.Status != "Cancelled" && evt.Status != "Draft")
        {
            throw new InvalidOperationException("Only cancelled or draft events can be deleted.");
        }

        _logger.LogInformation("Deleting Event {EventId} for Organizer {OrganizerId}", eventId, organizerId);
        await _repository.DeleteEventAsync(eventId);
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

        if (!ShowStatusTransitions.CanEdit(show.Status, out var editReason))
        {
            throw new InvalidOperationException(editReason);
        }

        if (dto.VenueId.HasValue && !await _venueService.VenueExistsAsync(dto.VenueId.Value))
        {
            throw new ArgumentException($"Venue '{dto.VenueId}' does not exist.", nameof(dto.VenueId));
        }

        show.ShowDate = dto.ShowDate;
        show.ShowTime = dto.ShowTime;
        show.VenueId = dto.VenueId;
        show.OnSaleAt = dto.OnSaleAt;
        show.HighDemandThreshold = dto.HighDemandThreshold;
        show.ReminderMinutesBefore = dto.ReminderMinutesBefore;

        await _repository.UpdateShowAsync(show);

        if (dto.Categories != null && dto.Categories.Count > 0)
        {
            var categories = new List<TicketCategory>();
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

                categories.Add(new TicketCategory
                {
                    Id = catDto.Id ?? Guid.Empty,
                    ShowId = showId,
                    Name = catDto.Name.Trim(),
                    Price = catDto.Price,
                    Capacity = catDto.Capacity
                });
            }

            await _repository.SaveTicketCategoriesAsync(showId, categories);
        }

        // Sync rules and threshold to Inventory service
        try
        {
            var allCategories = await _repository.GetTicketCategoriesByShowIdAsync(showId);
            if (allCategories.Count > 0)
            {
                var syncRequest = new InitializeShowStockRequest(
                    OrganizerId: organizerId,
                    OnSaleAt: show.OnSaleAt,
                    MaxPerCustomer: 6,
                    HoldMinutes: 10,
                    HighDemand: show.HighDemandThreshold.HasValue && show.HighDemandThreshold.Value > 0,
                    Categories: allCategories.Select(c => new InitializeShowStockCategory(
                        CategoryId: c.Id,
                        Capacity: c.Capacity,
                        UnitPrice: c.Price,
                        Currency: Currency,
                        AllocationMode: AllocationMode
                    )).ToList(),
                    HighDemandThreshold: show.HighDemandThreshold
                );
                await _inventoryClient.InitializeShowStockAsync(showId, syncRequest);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync updated show rules to Inventory service for show '{ShowId}'", showId);
        }
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

        if (!ShowStatusTransitions.CanTransition(show.Status, "Cancelled", out var transitionReason))
        {
            throw new InvalidOperationException(transitionReason);
        }

        _logger.LogInformation("Cancelling Show {ShowId} for Organizer {OrganizerId}", showId, organizerId);
        await _repository.UpdateShowStatusAsync(showId, "Cancelled");
    }
}
