using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Catalog.Service.Authorization;

namespace Catalog.Service.Controllers;

public record OrganizerAccountStatusResponse(string Status);

[ApiController]
[Route("api/catalog/organizer")]
public class OrganizerStatusController : ControllerBase
{
    /// <summary>
    /// Tells the signed-in organizer whether their account may manage events (<c>active</c>)
    /// or is read-only (<c>suspended</c>). The suspension reason is never disclosed here.
    /// </summary>
    [HttpGet("status")]
    [Authorize(Policy = "OrganizerRead")]
    [ProducesResponseType(typeof(OrganizerAccountStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public IActionResult GetStatus()
    {
        var suspended = HttpContext.Items.TryGetValue(ActiveOrganizerAuthorizationHandler.SuspendedItemKey, out var value) && value is true;
        return Ok(new OrganizerAccountStatusResponse(suspended ? "suspended" : "active"));
    }
}