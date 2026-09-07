using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Identity.Service.Clients;
using Identity.Service.Db;

namespace Identity.Service.Controllers;

[ApiController]
[Route("api/identity/organizer-requests")]
[Authorize(Roles = "Admin,admin")] // Requires the Admin or admin role
public class AdminController : ControllerBase
{
    private readonly IAccountRepository _repository;
    private readonly IWso2ScimClient _scimClient;
    private readonly ILogger<AdminController> _logger;

    public AdminController(IAccountRepository repository, IWso2ScimClient scimClient, ILogger<AdminController> logger)
    {
        _repository = repository;
        _scimClient = scimClient;
        _logger = logger;
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
            return StatusCode(500, new { message = "Failed to retrieve requests.", details = ex.Message });
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
                return BadRequest(new { message = $"Cannot approve a request that is already '{request.Status}'." });
            }

            var account = await _repository.GetUserAccountByIdAsync(request.UserAccountId);
            if (account == null)
            {
                return NotFound(new { message = "Associated user account not found." });
            }

            // 1. Update WSO2 IS status attribute to 'approved'
            await _scimClient.UpdateApprovalStatusAsync(account.Wso2Sub, "approved");

            // 2. Assign user to the 'Organizer' group/role in WSO2 IS
            await _scimClient.AssignUserToGroupAsync(account.Wso2Sub, account.Email, "Organizer");

            // 3. Update local database account role to 'Organizer' and approval status to 'approved'
            await _repository.UpdateUserAccountRoleAndStatusAsync(account.Id, "Organizer", "approved");

            // 4. Update the organizer request workflow state to 'approved'
            await _repository.UpdateOrganizerRequestStatusAsync(id, "approved");

            _logger.LogInformation("Successfully approved organizer request: {Id} for account {AccountId}", id, account.Id);
            return Ok(new { message = "Organizer request approved successfully." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to approve request {Id}", id);
            return StatusCode(500, new { message = "Failed to approve request.", details = ex.Message });
        }
    }

    /// <summary>
    /// Rejects an organizer request. Sets their status to 'rejected',
    /// and patches their WSO2 custom claim.
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
                return BadRequest(new { message = $"Cannot reject a request that is already '{request.Status}'." });
            }

            var account = await _repository.GetUserAccountByIdAsync(request.UserAccountId);
            if (account == null)
            {
                return NotFound(new { message = "Associated user account not found." });
            }

            // 1. Delete user account from WSO2 Asgardeo via SCIM
            await _scimClient.DeleteUserAsync(account.Wso2Sub);

            // 2. Delete user account and organizer request from the local database
            await _repository.DeleteUserAccountAsync(account.Id);

            _logger.LogInformation("Successfully rejected organizer request: {Id} for account {AccountId}", id, account.Id);
            return Ok(new { message = "Organizer request rejected successfully." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reject request {Id}", id);
            return StatusCode(500, new { message = "Failed to reject request.", details = ex.Message });
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
            return StatusCode(500, new { message = "Failed to retrieve organizers.", details = ex.Message });
        }
    }
}
