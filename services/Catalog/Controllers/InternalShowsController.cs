using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Catalog.Service.Services;

namespace Catalog.Service.Controllers;

[ApiController]
[Route("internal/catalog/shows")]
public class InternalShowsController : ControllerBase
{
    private readonly IEventService _eventService;

    public InternalShowsController(IEventService eventService)
    {
        _eventService = eventService;
    }

    [HttpGet("{showId}/sales-rules")]
    [Authorize(Policy = "InternalService")]
    [ProducesResponseType(typeof(ShowSalesRulesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSalesRules(Guid showId)
    {
        var salesRules = await _eventService.GetSalesRulesAsync(showId);
        if (salesRules is null)
        {
            return Problem(
                detail: $"Show '{showId}' was not found.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Show not found");
        }

        return Ok(salesRules);
    }
}
