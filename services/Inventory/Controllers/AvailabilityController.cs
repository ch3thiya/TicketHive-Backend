using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Inventory.Service.Services;

namespace Inventory.Service.Controllers;

[ApiController]
[Route("api/inventory/shows")]
public class AvailabilityController : ControllerBase
{
    private readonly IStockService _stockService;

    public AvailabilityController(IStockService stockService)
    {
        _stockService = stockService;
    }

    // Anonymous on purpose: customers browse events before signing in, and
    // the spec's endpoint table lists this as open to anyone (SCRUM-8).
    // Always reads current database state; never cached.
    [HttpGet("{showId}/availability")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAvailability(Guid showId)
    {
        var stock = await _stockService.GetAvailabilityAsync(showId);
        Response.Headers.CacheControl = "no-store";

        if (stock is null)
        {
            return Problem(
                detail: $"Show '{showId}' was not found.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Show not found");
        }

        return Ok(new AvailabilityResponse(showId, stock));
    }
}

public record AvailabilityResponse(Guid ShowId, System.Collections.Generic.List<StockItemResponse> Categories);
