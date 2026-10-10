using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Identity.Service.Db;
using Identity.Service.Models;

namespace Identity.Service.Controllers;

/// <summary>Authoritative organizer status: <c>approved</c> or <c>suspended</c>.</summary>
public record OrganizerLookupResponse(Guid OrganizerId, string Status);

public record OrganizerStatusBatchRequest(IReadOnlyList<Guid>? OrganizerIds);

// Service-to-service only: requires an Asgardeo client-credentials token whose
// scope matches Wso2:InternalApi:RequiredScope (see the InternalService policy
// in Program.cs). Development bypasses the policy, as in Catalog and Inventory.
[ApiController]
[Route("internal/identity/organizers")]
[Authorize(Policy = "InternalService")]
public class InternalOrganizersController : ControllerBase
{
    public const int MaxBatchSize = 200;

    private readonly IAccountRepository _repository;

    public InternalOrganizersController(IAccountRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// Reports the status of the organizer with the given Asgardeo subject. Approved and
    /// suspended organizers are returned with their status; 404 for everyone else,
    /// including pending and rejected applicants.
    /// </summary>
    [HttpGet("{sub}")]
    [ProducesResponseType(typeof(OrganizerLookupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrganizerBySub(string sub)
    {
        var account = await _repository.GetUserAccountBySubAsync(sub);
        return ToLookupResult(account);
    }

    [HttpGet("by-id/{id:guid}")]
    [ProducesResponseType(typeof(OrganizerLookupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrganizerById(Guid id)
    {
        var account = await _repository.GetUserAccountByIdAsync(id);
        return ToLookupResult(account);
    }

    /// <summary>
    /// Looks up many organizers at once. Unknown or non-organizer IDs are omitted.
    /// </summary>
    [HttpPost("status")]
    [ProducesResponseType(typeof(IReadOnlyList<OrganizerLookupResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetOrganizerStatuses([FromBody] OrganizerStatusBatchRequest? request)
    {
        var ids = request?.OrganizerIds?.Distinct().ToList();
        if (ids is null || ids.Count == 0 || ids.Count > MaxBatchSize)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid request",
                detail: $"Provide between 1 and {MaxBatchSize} organizer IDs.");
        }

        var statuses = await _repository.GetOrganizerStatusesAsync(ids);
        return Ok(statuses.Select(s => new OrganizerLookupResponse(s.Key, s.Value)).ToList());
    }

    private IActionResult ToLookupResult(UserAccount? account)
    {
        if (account == null || account.Role != "Organizer" || !OrganizerStatuses.IsKnownOrganizerStatus(account.ApprovalStatus))
        {
            return NotFound();
        }

        return Ok(new OrganizerLookupResponse(account.Id, account.ApprovalStatus));
    }
}
