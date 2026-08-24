using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Identity.Service.Clients;
using Identity.Service.Db;
using Identity.Service.Models;

namespace Identity.Service.Controllers;

[ApiController]
[Route("api/identity/accounts")]
public class AuthController : ControllerBase
{
    private readonly AccountRepository _repository;
    private readonly Wso2ScimClient _scimClient;
    private readonly ILogger<AuthController> _logger;

    public AuthController(AccountRepository repository, Wso2ScimClient scimClient, ILogger<AuthController> logger)
    {
        _repository = repository;
        _scimClient = scimClient;
        _logger = logger;
    }

    /// <summary>
    /// Synchronizes the user account from the JWT claims to the local database upon login.
    /// </summary>
    [HttpPost("sync")]
    [Authorize]
    public async Task<IActionResult> SyncAccount()
    {
        var subClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
            ?? User.FindFirst("sub")?.Value;
        var emailClaim = User.FindFirst(ClaimTypes.Email)?.Value 
            ?? User.FindFirst("email")?.Value;
        var nameClaim = User.FindFirst(ClaimTypes.Name)?.Value 
            ?? User.FindFirst("name")?.Value;

        if (string.IsNullOrEmpty(subClaim))
        {
            return BadRequest(new { message = "Subject claim ('sub' or 'NameIdentifier') is missing in access token." });
        }

        _logger.LogInformation("Syncing account for sub: {Sub}", subClaim);

        var existingAccount = await _repository.GetUserAccountBySubAsync(subClaim);
        if (existingAccount == null)
        {
            // First time login - provision user locally as Customer
            var newAccount = new UserAccount
            {
                Id = Guid.NewGuid(),
                Wso2Sub = subClaim,
                Email = emailClaim ?? "unknown@tickethive.com",
                FullName = nameClaim ?? "Unknown User",
                Role = "Customer",
                ApprovalStatus = "approved", // Customers are auto-approved
                CreatedAt = DateTime.UtcNow
            };

            await _repository.CreateUserAccountAsync(newAccount);
            return Ok(newAccount);
        }

        return Ok(existingAccount);
    }

    /// <summary>
    /// Custom signup form endpoint for Organizers.
    /// Creates the user in WSO2 IS in a 'pending' state and records their organizer request details locally.
    /// </summary>
    [HttpPost("register-organizer")]
    public async Task<IActionResult> RegisterOrganizer([FromBody] RegisterOrganizerRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        _logger.LogInformation("Registering organizer: {Email} for organization {Org}", request.Email, request.OrganizationName);

        try
        {
            // 1. Create user in WSO2 IS via SCIM 2.0 with isapproved = "pending"
            // Use the email as the username
            string wso2UserId = await _scimClient.CreateUserAsync(
                username: request.Email,
                password: request.Password,
                email: request.Email,
                fullName: request.FullName,
                initialStatus: "pending"
            );

            // 2. Create the local UserAccount
            var localAccount = new UserAccount
            {
                Id = Guid.NewGuid(),
                Wso2Sub = wso2UserId,
                Email = request.Email,
                FullName = request.FullName,
                Role = "Customer", // They start as a regular customer until approved
                ApprovalStatus = "pending",
                CreatedAt = DateTime.UtcNow
            };

            await _repository.CreateUserAccountAsync(localAccount);

            // 3. Create the OrganizerRequest record
            var organizerRequest = new OrganizerRequest
            {
                Id = Guid.NewGuid(),
                UserAccountId = localAccount.Id,
                OrganizationName = request.OrganizationName,
                BusinessEmail = request.BusinessEmail,
                Phone = request.Phone,
                EventType = request.EventType,
                About = request.About,
                Status = "pending",
                CreatedAt = DateTime.UtcNow
            };

            await _repository.CreateOrganizerRequestAsync(organizerRequest);

            return Ok(new { message = "Registration submitted successfully. The administrators will review your request.", wso2UserId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register organizer.");
            return StatusCode(500, new { message = "An error occurred during registration. Please try again.", details = ex.Message });
        }
    }
}

public record RegisterOrganizerRequest(
    string FullName,
    string Email,
    string Password,
    string OrganizationName,
    string BusinessEmail,
    string Phone,
    string EventType,
    string About
);
