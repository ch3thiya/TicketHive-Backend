using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Service.Controllers;

public record PingResponse(string Service, string Status);

// Minimal endpoint to prove the customer-facing authentication scheme works
// (AC7). Superseded once feature/scrum-8-live-availability adds real
// customer-facing endpoints.
[ApiController]
[Route("api/inventory/ping")]
[Authorize]
public class PingController : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(PingResponse), StatusCodes.Status200OK)]
    public IActionResult Get() => Ok(new PingResponse("Inventory Service", "Healthy"));
}
