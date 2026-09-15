using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Catalog.Service.Services;

namespace Catalog.Service.Controllers;

[ApiController]
[Route("api/catalog/venues")]
public class VenuesController : ControllerBase
{
    private readonly IVenueService _venueService;
    private readonly ILogger<VenuesController> _logger;

    public VenuesController(IVenueService venueService, ILogger<VenuesController> logger)
    {
        _venueService = venueService;
        _logger = logger;
    }

    [HttpGet]
    [Authorize]
    public async Task<IActionResult> GetVenues()
    {
        try
        {
            var venues = await _venueService.GetAllVenuesAsync();
            return Ok(venues);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing venues");
            return StatusCode(500, new { message = "An error occurred while retrieving venues." });
        }
    }

    [HttpGet("{id}")]
    [Authorize]
    public async Task<IActionResult> GetVenueById(Guid id)
    {
        try
        {
            var venue = await _venueService.GetVenueByIdAsync(id);
            if (venue == null)
            {
                return NotFound(new { message = $"Venue with ID '{id}' was not found." });
            }

            return Ok(venue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting venue {VenueId}", id);
            return StatusCode(500, new { message = "An error occurred while retrieving the venue." });
        }
    }

    [HttpPost]
    [Authorize(Roles = "Admin,admin")]
    public async Task<IActionResult> CreateVenue([FromBody] CreateVenueDto dto)
    {
        try
        {
            var venue = await _venueService.CreateVenueAsync(dto);
            return CreatedAtAction(nameof(GetVenueById), new { id = venue.Id }, venue);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating venue");
            return StatusCode(500, new { message = "An error occurred while creating the venue." });
        }
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin,admin")]
    public async Task<IActionResult> UpdateVenue(Guid id, [FromBody] UpdateVenueDto dto)
    {
        try
        {
            await _venueService.UpdateVenueAsync(id, dto);
            var updatedVenue = await _venueService.GetVenueByIdAsync(id);
            return Ok(updatedVenue);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating venue {VenueId}", id);
            return StatusCode(500, new { message = "An error occurred while updating the venue." });
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin,admin")]
    public async Task<IActionResult> DeleteVenue(Guid id)
    {
        try
        {
            await _venueService.DeleteVenueAsync(id);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                title: "Venue is in use");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting venue {VenueId}", id);
            return StatusCode(500, new { message = "An error occurred while deleting the venue." });
        }
    }
}
