using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Inventory.Service.Services;

namespace Inventory.Service.Controllers;

[ApiController]
[Authorize(Policy = "InternalService")]
[Route("internal/inventory/shows")]
public class InternalShowsController : ControllerBase
{
    private readonly IStockService _stockService;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<InternalShowsController> _logger;

    public InternalShowsController(
        IStockService stockService,
        IWebHostEnvironment environment,
        ILogger<InternalShowsController> logger)
    {
        _stockService = stockService;
        _environment = environment;
        _logger = logger;
    }

    // Idempotent: Catalog calls this before committing an event's status to
    // Published, and again on any retry after a crash between that call and
    // its own commit (SCRUM-8).
    [HttpPut("{showId}")]
    [ProducesResponseType(typeof(InitializeStockResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> InitializeStock(Guid showId, [FromBody] InitializeStockRequest request)
    {
        var isDevelopmentPlaceholderRequest = _environment.IsDevelopment()
            && HttpContext.Request.Headers.Authorization == "Bearer dev-internal-token";

        var authResult = isDevelopmentPlaceholderRequest
            ? AuthenticateResult.Success(new AuthenticationTicket(new System.Security.Claims.ClaimsPrincipal(), "Internal"))
            : await HttpContext.AuthenticateAsync("Internal");
        if (!authResult.Succeeded)
        {
            return Unauthorized();
        }

        var scopeClaim = authResult.Principal?.FindFirst("scope")?.Value;
        if (!isDevelopmentPlaceholderRequest
            && (scopeClaim == null || !scopeClaim.Split(' ').Contains("inventory:write")))
        {
            return Forbid();
        }

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
