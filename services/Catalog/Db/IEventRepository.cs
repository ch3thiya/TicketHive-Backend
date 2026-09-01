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
    Task UpdateEventAsync(Event evt);
    Task UpdateEventStatusAsync(Guid eventId, string status);

    Task<Show> CreateShowWithCategoriesAsync(Show show, List<TicketCategory> categories);
    Task<Show?> GetShowByIdAsync(Guid showId);
    Task<List<Show>> GetShowsByEventIdAsync(Guid eventId);
    Task<List<TicketCategory>> GetTicketCategoriesByShowIdAsync(Guid showId);
    Task UpdateShowAsync(Show show);
    Task ReplaceTicketCategoriesAsync(Guid showId, List<TicketCategory> categories);
    Task UpdateShowStatusAsync(Guid showId, string status);
}
