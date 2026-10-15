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
        if (venue.SeatMap is not null && dto.Capacity < venue.SeatMap.SeatCount)
        {
            throw new ArgumentException(
                $"Venue capacity cannot be lower than its {venue.SeatMap.SeatCount}-seat map. Edit or delete the seat map first.",
                nameof(dto.Capacity));
        }
        venue.Capacity = dto.Capacity;
        venue.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

        _logger.LogInformation("Updating venue {VenueId}", id);
        await _repository.UpdateVenueAsync(venue);
    }

    public async Task<VenueSeatMap> SaveSeatMapAsync(Guid venueId, SaveVenueSeatMapDto dto, string adminSub)
    {
        var venue = await _repository.GetVenueByIdAsync(venueId);
        if (venue is null)
        {
            throw new KeyNotFoundException($"Venue with ID '{venueId}' was not found.");
        }

        var spec = new VenueSeatMapSpec((dto.Sections ?? [])
            .Select(section => section with { Name = (section.Name ?? string.Empty).Trim() })
            .ToList());
        var errors = VenueSeatMapRules.Validate(spec, venue.Capacity);
        if (errors.Count > 0)
        {
            throw new VenueSeatMapValidationException(errors);
        }

        var now = _timeProvider.GetUtcNow();
        var map = await _repository.UpsertSeatMapAsync(
            venueId, spec, VenueSeatMapRules.SeatCount(spec), adminSub, now);
        _logger.LogInformation("Saved a {SeatCount}-seat map for venue {VenueId}", map.SeatCount, venueId);
        return map;
    }

    public async Task DeleteSeatMapAsync(Guid venueId)
    {
        if (!await _repository.VenueExistsAsync(venueId))
        {
            throw new KeyNotFoundException($"Venue with ID '{venueId}' was not found.");
        }

        if (!await _repository.DeleteSeatMapAsync(venueId))
        {
            throw new KeyNotFoundException("This venue does not have a seat map.");
        }

        _logger.LogInformation("Deleted the seat map for venue {VenueId}", venueId);
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

public sealed class VenueSeatMapValidationException(IReadOnlyList<LayoutError> errors) : Exception("The seat map is not valid.")
{
    public IReadOnlyList<LayoutError> Errors { get; } = errors;
}
