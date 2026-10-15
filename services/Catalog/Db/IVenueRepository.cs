using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Catalog.Service.Models;

namespace Catalog.Service.Db;

public record VenueDeleteResult(bool Deleted, int ReferencingShowCount);

public interface IVenueRepository
{
    Task<Venue> CreateVenueAsync(Venue venue);
    Task<Venue?> GetVenueByIdAsync(Guid id);
    Task<bool> VenueExistsAsync(Guid id);
    Task<List<Venue>> GetAllVenuesAsync();
    Task UpdateVenueAsync(Venue venue);
    Task<VenueSeatMap> UpsertSeatMapAsync(Guid venueId, VenueSeatMapSpec spec, int seatCount, string createdBy, DateTimeOffset now);
    Task<bool> DeleteSeatMapAsync(Guid venueId);

    // Locks the venue row, counts shows that reference it, and deletes it
    // only if that count is zero — all inside one transaction, so a show
    // cannot start referencing the venue between the count and the delete.
    // The shows.venue_id foreign key is the backstop if a reference slips
    // in anyway. Returns null if the venue does not exist.
    Task<VenueDeleteResult?> DeleteVenueIfUnusedAsync(Guid id);
}
