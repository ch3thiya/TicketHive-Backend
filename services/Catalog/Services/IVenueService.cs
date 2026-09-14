using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Catalog.Service.Models;

namespace Catalog.Service.Services;

public record CreateVenueDto(string Name, string Address, int Capacity);
public record UpdateVenueDto(string Name, string Address, int Capacity);

public interface IVenueService
{
    Task<Venue> CreateVenueAsync(CreateVenueDto dto);
    Task<List<Venue>> GetAllVenuesAsync();
    Task<Venue?> GetVenueByIdAsync(Guid id);
    Task<bool> VenueExistsAsync(Guid id);
    Task UpdateVenueAsync(Guid id, UpdateVenueDto dto);

    // Throws KeyNotFoundException if the venue does not exist, or
    // InvalidOperationException (naming how many shows use it) if it is
    // still referenced. Deletes only when neither applies.
    Task DeleteVenueAsync(Guid id);
}
