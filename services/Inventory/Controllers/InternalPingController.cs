using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Service.Controllers;

// Minimal endpoint to prove the internal authentication scheme and the
// InternalService policy work (AC6). Superseded once
// feature/scrum-8-live-availability adds the real internal endpoints.
[ApiController]
[Route("internal/inventory/ping")]
[Authorize(Policy = "InternalService")]
public class InternalPingController : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(PingResponse), StatusCodes.Status200OK)]
    public IActionResult Get() => Ok(new PingResponse("Inventory Service", "Healthy"));
}
