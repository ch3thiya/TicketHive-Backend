using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Catalog.Service.Services;

namespace Catalog.Service.Controllers;

[ApiController]
[Route("api/catalog/events")]
public class EventsController : ControllerBase
{
    private readonly IEventService _eventService;
    private readonly ILogger<EventsController> _logger;
    private readonly IWebHostEnvironment _env;

    public EventsController(IEventService eventService, ILogger<EventsController> logger, IWebHostEnvironment env)
    {
        _eventService = eventService;
        _logger = logger;
        _env = env;
    }

    private Guid GetCurrentOrganizerId()
    {
        // 1. Primary: Extract sub or NameIdentifier from authenticated JWT token claims
        var subClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;

        if (!string.IsNullOrEmpty(subClaim))
        {
            if (Guid.TryParse(subClaim, out var parsedGuid))
            {
                return parsedGuid;
            }

            // Deterministic GUID based on subject claim string (e.g. Asgardeo/WSO2 user ID)
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(subClaim));
            return new Guid(hash);
        }

        // 2. Development/Testing Fallback only: Allow X-Organizer-Id header if running locally in Development
        if (_env.IsDevelopment() &&
            Request.Headers.TryGetValue("X-Organizer-Id", out var headerVal) &&
            Guid.TryParse(headerVal.ToString(), out var headerGuid))
        {
            return headerGuid;
        }

        throw new UnauthorizedAccessException("Organizer identity could not be determined from access token.");
    }

    [HttpPost]
    public async Task<IActionResult> CreateEvent([FromBody] CreateEventDto dto)
    {
        try
        {
            var organizerId = GetCurrentOrganizerId();
            var evt = await _eventService.CreateEventAsync(organizerId, dto);
            return CreatedAtAction(nameof(GetEventById), new { eventId = evt.Id }, evt);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating event");
            return StatusCode(500, new { message = "An error occurred while creating the event.", details = ex.Message });
        }
    }

    [HttpPost("{eventId}/shows")]
    public async Task<IActionResult> CreateShow(Guid eventId, [FromBody] CreateShowRequestDto dto)
    {
        try
        {
            var organizerId = GetCurrentOrganizerId();
            var show = await _eventService.CreateShowAsync(organizerId, eventId, dto);
            return Ok(show);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating show for event {EventId}", eventId);
            return StatusCode(500, new { message = "An error occurred while creating the show.", details = ex.Message });
        }
    }

    [HttpGet("my-events")]
    public async Task<IActionResult> GetMyEvents()
    {
        try
        {
            var organizerId = GetCurrentOrganizerId();
            var events = await _eventService.GetEventsByOrganizerIdAsync(organizerId);
            return Ok(events);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting organizer events");
            return StatusCode(500, new { message = "An error occurred while retrieving your events." });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetAllPublishedEvents(
        [FromQuery] string? search = null,
        [FromQuery] string? category = null,
        [FromQuery] string? fromDate = null,
        [FromQuery] string? toDate = null,
        [FromQuery] Guid? venueId = null)
    {
        try
        {
            // Parse optional date filters
            DateOnly? parsedFromDate = null;
            DateOnly? parsedToDate = null;

            if (!string.IsNullOrWhiteSpace(fromDate))
            {
                if (!DateOnly.TryParse(fromDate, out var fd))
                    return BadRequest(new { message = "Invalid 'fromDate' format. Use YYYY-MM-DD." });
                parsedFromDate = fd;
            }

            if (!string.IsNullOrWhiteSpace(toDate))
            {
                if (!DateOnly.TryParse(toDate, out var td))
                    return BadRequest(new { message = "Invalid 'toDate' format. Use YYYY-MM-DD." });
                parsedToDate = td;
            }

            bool hasFilters = !string.IsNullOrWhiteSpace(search) ||
                              !string.IsNullOrWhiteSpace(category) ||
                              parsedFromDate.HasValue ||
                              parsedToDate.HasValue ||
                              venueId.HasValue;

            var events = hasFilters
                ? await _eventService.GetPublishedEventsAsync(search, category, parsedFromDate, parsedToDate, venueId)
                : await _eventService.GetAllPublishedEventsAsync();

            return Ok(events);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting published events");
            return StatusCode(500, new { message = "An error occurred while retrieving published events." });
        }
    }

    /// <summary>
    /// Public event detail endpoint. Returns only Published events.
    /// Draft and Cancelled events return 404 to prevent information leakage.
    /// Note: Organizer-specific event detail is served via GET /my-events which returns all statuses.
    /// </summary>
    [HttpGet("{eventId}")]
    public async Task<IActionResult> GetEventById(Guid eventId)
    {
        try
        {
            var evt = await _eventService.GetPublishedEventByIdAsync(eventId);
            if (evt == null)
            {
                return NotFound(new { message = $"Event with ID '{eventId}' was not found." });
            }

            return Ok(evt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting event {EventId}", eventId);
            return StatusCode(500, new { message = "An error occurred while retrieving event details." });
        }
    }

    [HttpPost("{eventId}/publish")]
    public async Task<IActionResult> PublishEvent(Guid eventId)
    {
        try
        {
            var organizerId = GetCurrentOrganizerId();
            await _eventService.PublishEventAsync(organizerId, eventId);
            return Ok(new { message = "Event published successfully.", eventId, status = "Published" });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing event {EventId}", eventId);
            return StatusCode(500, new { message = "An error occurred while publishing the event." });
        }
    }

    [HttpPut("{eventId}")]
    public async Task<IActionResult> UpdateEvent(Guid eventId, [FromBody] UpdateEventDto dto)
    {
        try
        {
            var organizerId = GetCurrentOrganizerId();
            await _eventService.UpdateEventAsync(organizerId, eventId, dto);
            return Ok(new { message = "Event updated successfully.", eventId });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating event {EventId}", eventId);
            return StatusCode(500, new { message = "An error occurred while updating the event." });
        }
    }

    [HttpPost("{eventId}/cancel")]
    public async Task<IActionResult> CancelEvent(Guid eventId)
    {
        try
        {
            var organizerId = GetCurrentOrganizerId();
            await _eventService.CancelEventAsync(organizerId, eventId);
            return Ok(new { message = "Event cancelled successfully.", eventId, status = "Cancelled" });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling event {EventId}", eventId);
            return StatusCode(500, new { message = "An error occurred while cancelling the event." });
        }
    }

    [HttpPut("/api/catalog/shows/{showId}")]
    public async Task<IActionResult> UpdateShow(Guid showId, [FromBody] UpdateShowDto dto)
    {
        try
        {
            var organizerId = GetCurrentOrganizerId();
            await _eventService.UpdateShowAsync(organizerId, showId, dto);
            return Ok(new { message = "Show updated successfully.", showId });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating show {ShowId}", showId);
            return StatusCode(500, new { message = "An error occurred while updating the show." });
        }
    }

    [HttpPost("/api/catalog/shows/{showId}/cancel")]
    public async Task<IActionResult> CancelShow(Guid showId)
    {
        try
        {
            var organizerId = GetCurrentOrganizerId();
            await _eventService.CancelShowAsync(organizerId, showId);
            return Ok(new { message = "Show cancelled successfully.", showId, status = "Cancelled" });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling show {ShowId}", showId);
            return StatusCode(500, new { message = "An error occurred while cancelling the show." });
        }
    }
}
