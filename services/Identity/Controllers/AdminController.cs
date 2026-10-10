using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Identity.Service.Clients;
using Identity.Service.Db;
using Identity.Service.Models;
using Identity.Service.Services;
using System.Security.Claims;

namespace Identity.Service.Controllers;

public record SuspendOrganizerRequest(string? Reason);
public record ReinstateOrganizerRequest(string? Note);
public record OrganizerStatusResponse(Guid OrganizerId, string Status, bool Repeated);

[ApiController]
[Route("api/identity/organizer-requests")]
[Authorize(Roles = "Admin,admin")] // Requires the Admin or admin role
public class AdminController : ControllerBase
{
    private readonly IAccountRepository _repository;
    private readonly IWso2ScimClient _scimClient;
    private readonly IOrganizerSuspensionService _suspensionService;
    private readonly ILogger<AdminController> _logger;

    public AdminController(IAccountRepository repository, IWso2ScimClient scimClient, IOrganizerSuspensionService suspensionService, ILogger<AdminController> logger)
    {
        _repository = repository;
        _scimClient = scimClient;
        _suspensionService = suspensionService;
        _logger = logger;
    }

    /// <summary>
    /// Suspends an approved organizer. The reason is mandatory and is recorded with the
    /// acting admin and a timestamp. Repeating the request is harmless.
    /// </summary>
    [HttpPost("organizers/{id:guid}/suspend")]
    [ProducesResponseType(typeof(OrganizerStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SuspendOrganizer(Guid id, [FromBody] SuspendOrganizerRequest? request)
    {
        var actor = GetActorSub();
        if (actor is null)
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized", detail: "The acting administrator could not be identified.");
        }

        var result = await _suspensionService.SuspendAsync(id, actor, request?.Reason);
        return ToResponse(id, result);
    }

    /// <summary>
    /// Reinstates a suspended organizer. Cancelled shows stay cancelled and draft shows
    /// stay drafts; only management and sales eligibility resume.
    /// </summary>
    [HttpPost("organizers/{id:guid}/reinstate")]
    [ProducesResponseType(typeof(OrganizerStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReinstateOrganizer(Guid id, [FromBody] ReinstateOrganizerRequest? request = null)
    {
        var actor = GetActorSub();
        if (actor is null)
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized", detail: "The acting administrator could not be identified.");
        }

        var result = await _suspensionService.ReinstateAsync(id, actor, request?.Note);
        return ToResponse(id, result);
    }

    /// <summary>
    /// Returns the durable suspension/reinstatement history for one organizer.
    /// </summary>
    [HttpGet("organizers/{id:guid}/status-history")]
    [ProducesResponseType(typeof(IReadOnlyList<OrganizerStatusAuditEntry>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrganizerStatusHistory(Guid id)
    {
        var history = await _suspensionService.GetHistoryAsync(id);
        if (history is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Organizer not found", detail: "No organizer exists with that identifier.");
        }

        return Ok(history);
    }

    private string? GetActorSub()
    {
        var sub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        return string.IsNullOrWhiteSpace(sub) ? null : sub;
    }

    private IActionResult ToResponse(Guid organizerId, OrganizerStatusChangeResult result)
    {
        return result.Outcome switch
        {
            OrganizerStatusChangeOutcome.Changed => Ok(new OrganizerStatusResponse(organizerId, result.Status!, false)),
            OrganizerStatusChangeOutcome.Unchanged => Ok(new OrganizerStatusResponse(organizerId, result.Status!, true)),
            OrganizerStatusChangeOutcome.NotFound => Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Organizer not found",
                detail: "No organizer exists with that identifier."),
            OrganizerStatusChangeOutcome.InvalidReason => Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid reason",
                detail: $"A reason of 1 to {OrganizerSuspensionService.MaxReasonLength} characters is required."),
            _ => Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Invalid status change",
                detail: "Only approved organizers can be suspended and only suspended organizers can be reinstated.")
        };
    }
    /// <summary>
    /// Lists all organizer requests that are pending approval.
    /// </summary>
    [HttpGet("pending")]
    public async Task<IActionResult> GetPendingRequests()
    {
        _logger.LogInformation("Admin requested pending organizer requests");
        try
        {
            var requests = await _repository.GetPendingOrganizerRequestsAsync();
            return Ok(requests);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get pending organizer requests.");
            return Problem(
                detail: "Failed to retrieve requests.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Unexpected error");
        }
    }

    /// <summary>
    /// Approves an organizer request. Sets their status to 'approved',
    /// patches their WSO2 custom claim, and assigns them the 'Organizer' role.
    /// </summary>
    [HttpPost("{id}/approve")]
    public async Task<IActionResult> ApproveRequest(Guid id)
    {
        _logger.LogInformation("Approving organizer request: {Id}", id);

        try
        {
            var request = await _repository.GetOrganizerRequestByIdAsync(id);
            if (request == null)
            {
                return NotFound(new { message = "Organizer request not found." });
            }

            if (request.Status != "pending")
            {
                return Problem(
                    detail: $"Cannot approve a request that is already '{request.Status}'.",
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Request already decided");
            }

            var account = await _repository.GetUserAccountByIdAsync(request.UserAccountId);
            if (account == null)
            {
                return NotFound(new { message = "Associated user account not found." });
            }

            // Grant access in Asgardeo first. Both calls are safe to repeat on
            // retry (the attribute patch is a replace; group assignment checks
            // membership first), and nothing local is written until both
            // succeed, so a failure here leaves the request exactly "pending".
            try
            {
                await _scimClient.UpdateApprovalStatusAsync(account.Wso2Sub, "approved");
                await _scimClient.AssignUserToGroupAsync(account.Wso2Sub, account.Email, "Organizer");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to grant organizer access in Asgardeo for request {Id}", id);
                return Problem(
                    detail: "Approving the organizer did not complete because the identity provider could not be updated. Nothing was changed locally; retry the approval.",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Organizer approval incomplete");
            }

            // Record the grant locally. The request status is written last, so
            // it alone marks the whole approval as finished; both writes are
            // plain idempotent updates, safe for a retry to redo. A failure
            // here means Asgardeo already granted access with no local record
            // of it yet, so it is logged distinctly for follow-up.
            try
            {
                await _repository.UpdateUserAccountRoleAndStatusAsync(account.Id, "Organizer", "approved");
                await _repository.UpdateOrganizerRequestStatusAsync(id, "approved");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Organizer request {Id} account {AccountId} was granted access in Asgardeo but the local record was not updated", id, account.Id);
                return Problem(
                    detail: "The organizer was granted access in the identity provider, but the local record was not updated. Retrying will complete the approval.",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Organizer approval incomplete");
            }

            _logger.LogInformation("Successfully approved organizer request: {Id} for account {AccountId}", id, account.Id);
            return Ok(new { message = "Organizer request approved successfully." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to approve request {Id}", id);
            return Problem(
                detail: "Failed to approve request.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Unexpected error");
        }
    }

    /// <summary>
    /// Rejects an organizer request. Sets the request's own status to 'rejected'
    /// and resets the applicant's account to the plain-customer shape it had
    /// before applying, without touching their Asgardeo identity.
    /// </summary>
    [HttpPost("{id}/reject")]
    public async Task<IActionResult> RejectRequest(Guid id)
    {
        _logger.LogInformation("Rejecting organizer request: {Id}", id);

        try
        {
            var request = await _repository.GetOrganizerRequestByIdAsync(id);
            if (request == null)
            {
                return NotFound(new { message = "Organizer request not found." });
            }

            if (request.Status != "pending")
            {
                return Problem(
                    detail: $"Cannot reject a request that is already '{request.Status}'.",
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Request already decided");
            }

            // Applying moved the account's approval status to 'pending'; reset it to
            // the plain-customer value so the applicant is indistinguishable from a
            // user who never applied. Role is already 'Customer' and stays that way.
            await _repository.UpdateUserAccountRoleAndStatusAsync(request.UserAccountId, "Customer", "approved");
            await _repository.UpdateOrganizerRequestStatusAsync(id, "rejected");

            _logger.LogInformation("Successfully rejected organizer request: {Id}", id);
            return Ok(new { message = "Organizer request rejected successfully." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reject request {Id}", id);
            return Problem(
                detail: "Failed to reject request.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Unexpected error");
        }
    }

    /// <summary>
    /// Lists all approved organizers.
    /// </summary>
    [HttpGet("organizers")]
    public async Task<IActionResult> GetApprovedOrganizers()
    {
        _logger.LogInformation("Admin requested approved organizers list");
        try
        {
            var organizers = await _repository.GetApprovedOrganizersAsync();
            return Ok(organizers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get approved organizers.");
            return Problem(
                detail: "Failed to retrieve organizers.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Unexpected error");
        }
    }
}
