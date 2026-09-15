using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Inventory.Service.Services;

namespace Inventory.Service.Controllers;

[ApiController]
[Route("api/inventory/holds")]
public class HoldsController : ControllerBase
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private const string AdmissionTokenHeader = "Admission-Token";

    private readonly IHoldService _holdService;

    public HoldsController(IHoldService holdService)
    {
        _holdService = holdService;
    }

    // Customer authentication (the default scheme).
    [HttpPost]
    [Authorize]
    [ProducesResponseType(typeof(HoldResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateHold([FromBody] CreateHoldRequest request)
    {
        if (!Request.Headers.TryGetValue(IdempotencyKeyHeader, out var idempotencyKeyValues) ||
            string.IsNullOrWhiteSpace(idempotencyKeyValues))
        {
            return Problem(
                detail: $"The '{IdempotencyKeyHeader}' header is required.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Missing idempotency key");
        }

        var customerSub = GetCustomerSub();
        var hasAdmissionToken = Request.Headers.ContainsKey(AdmissionTokenHeader);

        try
        {
            var result = await _holdService.CreateHoldAsync(customerSub, idempotencyKeyValues.ToString(), hasAdmissionToken, request);

            return result.Status switch
            {
                CreateHoldStatus.Created or CreateHoldStatus.Duplicate =>
                    CreatedAtAction(nameof(GetHold), new { holdId = result.Hold!.HoldId }, result.Hold),
                CreateHoldStatus.ShowNotFound => Problem(
                    detail: $"Show '{request.ShowId}' was not found.",
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Show not found"),
                CreateHoldStatus.CategoryNotFound => Problem(
                    detail: $"Category '{result.CategoryId}' is not part of show '{request.ShowId}'.",
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Category not found"),
                CreateHoldStatus.HighDemandBlocked => Problem(
                    detail: "This show requires a valid admission token before holding tickets.",
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Admission required"),
                CreateHoldStatus.StockUnavailable => Problem(
                    detail: $"Category '{result.CategoryId}' no longer has enough tickets available.",
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Tickets no longer available"),
                CreateHoldStatus.QuotaExceeded => Problem(
                    detail: $"This request would exceed the per-customer limit of {result.Limit} tickets for this show.",
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Per-customer limit exceeded"),
                _ => throw new InvalidOperationException($"Unhandled hold creation status '{result.Status}'.")
            };
        }
        catch (ArgumentException ex)
        {
            return Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid hold request");
        }
    }

    [HttpGet("{holdId}")]
    [Authorize]
    [ProducesResponseType(typeof(HoldResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHold(Guid holdId)
    {
        var customerSub = GetCustomerSub();
        var hold = await _holdService.GetHoldAsync(holdId);

        // A 403 would confirm the hold exists to anyone probing ids, so a
        // non-owner gets the same 404 as a hold that was never created.
        if (hold is null || hold.CustomerSub != customerSub)
        {
            return Problem(
                detail: $"Hold '{holdId}' was not found.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Hold not found");
        }

        return Ok(HoldService.ToResponse(hold));
    }

    private string GetCustomerSub() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("sub")?.Value
        ?? string.Empty;
}
