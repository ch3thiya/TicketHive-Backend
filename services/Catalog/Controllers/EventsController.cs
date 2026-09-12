using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Catalog.Service.Authorization;
using Catalog.Service.Services;

namespace Catalog.Service.Controllers;

[ApiController]
[Route("api/catalog/events")]
public class EventsController : ControllerBase
{
    private readonly IEventService _eventService;
    private readonly ILogger<EventsController> _logger;

    public EventsController(IEventService eventService, ILogger<EventsController> logger)
    {
        _eventService = eventService;
        _logger = logger;
    }

    // The active-organizer policy resolves and validates the caller's organizer id
    // against Identity; this just reads back what it already stashed on the request.
    private Guid GetCurrentOrganizerId()
    {
        if (HttpContext.Items.TryGetValue(ActiveOrganizerAuthorizationHandler.OrganizerIdItemKey, out var value) &&
            value is Guid organizerId)
        {
            return organizerId;
        }

        throw new UnauthorizedAccessException("Organizer identity could not be determined from access token.");
    }

    [HttpPost]
    [Authorize(Policy = "ActiveOrganizer")]
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
    [Authorize(Policy = "ActiveOrganizer")]
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
    [Authorize(Policy = "ActiveOrganizer")]
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
    [AllowAnonymous]
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
            return StatusCode(500, new { message = "An error occurred while retrieving published events.", details = ex.Message });
        }
    }

    /// <summary>
    /// Public event detail endpoint. Returns only Published events.
    /// Draft and Cancelled events return 404 to prevent information leakage.
    /// Note: Organizer-specific event detail is served via GET /my-events which returns all statuses.
    /// </summary>
    [HttpGet("{eventId}")]
    [AllowAnonymous]
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
    [Authorize(Policy = "ActiveOrganizer")]
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
            return Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                title: "Invalid event status transition");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing event {EventId}", eventId);
            return StatusCode(500, new { message = "An error occurred while publishing the event." });
        }
    }

    [HttpPut("{eventId}")]
    [Authorize(Policy = "ActiveOrganizer")]
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
        catch (InvalidOperationException ex)
        {
            return Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                title: "Invalid event status transition");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating event {EventId}", eventId);
            return StatusCode(500, new { message = "An error occurred while updating the event." });
        }
    }

    [HttpPost("{eventId}/cancel")]
    [Authorize(Policy = "ActiveOrganizer")]
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
        catch (InvalidOperationException ex)
        {
            return Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                title: "Invalid event status transition");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling event {EventId}", eventId);
            return StatusCode(500, new { message = "An error occurred while cancelling the event." });
        }
    }

    [HttpPut("/api/catalog/shows/{showId}")]
    [Authorize(Policy = "ActiveOrganizer")]
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
        catch (InvalidOperationException ex)
        {
            return Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                title: "Invalid show status transition");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating show {ShowId}", showId);
            return StatusCode(500, new { message = "An error occurred while updating the show." });
        }
    }

    [HttpPost("/api/catalog/shows/{showId}/cancel")]
    [Authorize(Policy = "ActiveOrganizer")]
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
        catch (InvalidOperationException ex)
        {
            return Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                title: "Invalid show status transition");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling show {ShowId}", showId);
            return StatusCode(500, new { message = "An error occurred while cancelling the show." });
        }
    }
}
