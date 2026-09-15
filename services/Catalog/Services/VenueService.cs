using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Catalog.Service.Db;
using Catalog.Service.Models;

namespace Catalog.Service.Services;

public class VenueService : IVenueService
{
    private readonly IVenueRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<VenueService> _logger;

    public VenueService(IVenueRepository repository, TimeProvider timeProvider, ILogger<VenueService> logger)
    {
        _repository = repository;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<Venue> CreateVenueAsync(CreateVenueDto dto)
    {
        var name = ValidateName(dto.Name);
        var address = ValidateAddress(dto.Address);
        ValidateCapacity(dto.Capacity);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var venue = new Venue
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Address = address,
            Capacity = dto.Capacity,
            CreatedAt = now,
            UpdatedAt = now
        };

        _logger.LogInformation("Creating venue '{Name}'", venue.Name);
        return await _repository.CreateVenueAsync(venue);
    }

    public Task<List<Venue>> GetAllVenuesAsync() => _repository.GetAllVenuesAsync();

    public Task<Venue?> GetVenueByIdAsync(Guid id) => _repository.GetVenueByIdAsync(id);

    public Task<bool> VenueExistsAsync(Guid id) => _repository.VenueExistsAsync(id);

    public async Task UpdateVenueAsync(Guid id, UpdateVenueDto dto)
    {
        var venue = await _repository.GetVenueByIdAsync(id);
        if (venue == null)
        {
            throw new KeyNotFoundException($"Venue with ID '{id}' was not found.");
        }

        venue.Name = ValidateName(dto.Name);
        venue.Address = ValidateAddress(dto.Address);
        ValidateCapacity(dto.Capacity);
        venue.Capacity = dto.Capacity;
        venue.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

        _logger.LogInformation("Updating venue {VenueId}", id);
        await _repository.UpdateVenueAsync(venue);
    }

    public async Task DeleteVenueAsync(Guid id)
    {
        var result = await _repository.DeleteVenueIfUnusedAsync(id);
        if (result == null)
        {
            throw new KeyNotFoundException($"Venue with ID '{id}' was not found.");
        }

        if (!result.Deleted)
        {
            throw new InvalidOperationException(
                $"Cannot delete venue '{id}': {result.ReferencingShowCount} show(s) still reference it.");
        }

        _logger.LogInformation("Deleted venue {VenueId}", id);
    }

    private static string ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Venue name is required.", nameof(name));
        }

        return name.Trim();
    }

    private static string ValidateAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("Venue address is required.", nameof(address));
        }

        return address.Trim();
    }

    private static void ValidateCapacity(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentException("Venue capacity must be positive.", nameof(capacity));
        }
    }
}
