using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Inventory.Service.Services;

namespace Inventory.Service.Controllers;

[ApiController]
[Route("internal/inventory/shows")]
public class InternalShowsController : ControllerBase
{
    private readonly IStockService _stockService;
    private readonly ILogger<InternalShowsController> _logger;

    public InternalShowsController(IStockService stockService, ILogger<InternalShowsController> logger)
    {
        _stockService = stockService;
        _logger = logger;
    }

    // Idempotent: Catalog calls this before committing an event's status to
    // Published, and again on any retry after a crash between that call and
    // its own commit (SCRUM-8).
    [HttpPut("{showId}")]
    [Authorize(Policy = "InternalService")]
    [ProducesResponseType(typeof(InitializeStockResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> InitializeStock(Guid showId, [FromBody] InitializeStockRequest request)
    {
        try
        {
            var stock = await _stockService.InitializeAsync(showId, request);
            return Ok(new InitializeStockResponse(showId, stock));
        }
        catch (ArgumentException ex)
        {
            return Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid stock initialization request");
        }
    }
}

public record InitializeStockResponse(Guid ShowId, System.Collections.Generic.List<StockItemResponse> Stock);
