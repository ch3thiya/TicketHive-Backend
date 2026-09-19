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

    // Fetches shows for every given event in one query, grouped by event id.
    // Used by listing endpoints to avoid one shows query per event.
    Task<Dictionary<Guid, List<Show>>> GetShowsByEventIdsAsync(IReadOnlyCollection<Guid> eventIds);

    Task<List<TicketCategory>> GetTicketCategoriesByShowIdAsync(Guid showId);

    // Fetches active ticket categories for every given show in one query,
    // grouped by show id. Used by listing endpoints to avoid one categories
    // query per show.
    Task<Dictionary<Guid, List<TicketCategory>>> GetTicketCategoriesByShowIdsAsync(IReadOnlyCollection<Guid> showIds);

    Task UpdateShowAsync(Show show);

    // Reconciles ticket_categories for a show against the given list, inside one
    // transaction: categories with Id == Guid.Empty are inserted with a new id
    // (Guid.CreateVersion7()), others are matched to an existing row of this show
    // and updated in place, and active rows absent from the list are retired
    // (is_active = false). Throws ArgumentException if an id does not belong to
    // this show or belongs to a retired category; nothing is written in that case.
    Task SaveTicketCategoriesAsync(Guid showId, List<TicketCategory> categories);
    Task UpdateShowStatusAsync(Guid showId, string status);
    Task DeleteEventAsync(Guid eventId);
}
