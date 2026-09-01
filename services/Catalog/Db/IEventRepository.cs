using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Catalog.Service.Models;

namespace Catalog.Service.Db;

public interface IEventRepository
{
    Task<Event> CreateEventAsync(Event evt);
    Task<Event?> GetEventByIdAsync(Guid id);
    Task<List<Event>> GetEventsByOrganizerIdAsync(Guid organizerId);
    Task<List<Event>> GetAllPublishedEventsAsync();
    Task<List<Event>> GetPublishedEventsAsync(string? search, string? category, DateOnly? fromDate, DateOnly? toDate, Guid? venueId);
    Task<Event?> GetPublishedEventByIdAsync(Guid id);
    Task UpdateEventAsync(Event evt);
    Task UpdateEventStatusAsync(Guid eventId, string status);

    Task<Show> CreateShowWithCategoriesAsync(Show show, List<TicketCategory> categories);
    Task<Show?> GetShowByIdAsync(Guid showId);
    Task<List<Show>> GetShowsByEventIdAsync(Guid eventId);
    Task<List<TicketCategory>> GetTicketCategoriesByShowIdAsync(Guid showId);
    Task UpdateShowAsync(Show show);
    Task UpdateShowStatusAsync(Guid showId, string status);
}
