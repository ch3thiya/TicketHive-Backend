using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Identity.Service.Clients;
using Identity.Service.Db;
using Identity.Service.Models;
using System.Text.Json;

namespace Identity.Service.Controllers;

[ApiController]
[Route("api/identity/accounts")]
public class AuthController : ControllerBase
{
    private readonly IAccountRepository _repository;
    private readonly IWso2ScimClient _scimClient;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IAccountRepository repository, IWso2ScimClient scimClient, ILogger<AuthController> logger)
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

        // Extract roles from token claims (e.g. groups or roles claims from Asgardeo)
        var roles = User.FindAll("groups").Select(c => c.Value)
            .Concat(User.FindAll(ClaimTypes.Role).Select(c => c.Value))
            .Concat(User.FindAll("roles").Select(c => c.Value))
            .ToList();

        string tokenRole = "Customer";
        if (roles.Any(r => r.Equals("Admin", StringComparison.OrdinalIgnoreCase) || r.Equals("Admins", StringComparison.OrdinalIgnoreCase)))
        {
            tokenRole = "Admin";
        }
        else if (roles.Any(r => r.Equals("Organizer", StringComparison.OrdinalIgnoreCase) || r.Equals("Organizers", StringComparison.OrdinalIgnoreCase)))
        {
            tokenRole = "Organizer";
        }

        var existingAccount = await _repository.GetUserAccountBySubAsync(subClaim);
        if (existingAccount == null)
        {
            // First time login - provision user locally using claims-based role
            var newAccount = new UserAccount
            {
                Id = Guid.NewGuid(),
                Wso2Sub = subClaim,
                Email = emailClaim ?? "unknown@tickethive.com",
                FullName = nameClaim ?? "Unknown User",
                Role = tokenRole,
                ApprovalStatus = "approved",
                CreatedAt = DateTime.UtcNow
            };

            await _repository.CreateUserAccountAsync(newAccount);
            return Ok(newAccount);
        }

        // If user was assigned elevated role in Asgardeo (e.g. Admin or Organizer), sync database
        if (!existingAccount.Role.Equals(tokenRole, StringComparison.OrdinalIgnoreCase) && tokenRole != "Customer")
        {
            existingAccount.Role = tokenRole;
            existingAccount.ApprovalStatus = "approved";
            await _repository.UpdateUserAccountRoleAndStatusAsync(existingAccount.Id, tokenRole, "approved");
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

        // Pre-check if an account with this email already exists in our local database
        var existingAccount = await _repository.GetUserAccountByEmailAsync(request.Email);
        if (existingAccount != null)
        {
            return BadRequest(new { message = $"An account with the email '{request.Email}' already exists in the system." });
        }

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
            
            // Extract clean SCIM error message details if returned by Asgardeo
            var message = ex.Message;
            if (message.Contains("Failed to create user in identity provider:"))
            {
                var jsonPart = message.Replace("Failed to create user in identity provider:", "").Trim();
                try
                {
                    using var doc = JsonDocument.Parse(jsonPart);
                    if (doc.RootElement.TryGetProperty("detail", out var detailProp))
                    {
                        var scimError = detailProp.GetString();
                        if (!string.IsNullOrEmpty(scimError))
                        {
                            // If it is a duplicate account warning, translate the masked username to a clean email warning
                            if (scimError.Contains("already exists"))
                            {
                                return BadRequest(new { message = $"An account with the email '{request.Email}' already exists in the system." });
                            }
                            return BadRequest(new { message = scimError });
                        }
                    }
                }
                catch
                {
                    // Fail-safe to return default message
                }
            }

            return Problem(
                detail: "An error occurred during registration. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Registration failed");
        }
    }

    /// <summary>
    /// Custom signup form endpoint for Customers.
    /// Creates the user in WSO2 IS in an 'approved' state and records their account details locally.
    /// </summary>
    [HttpPost("register-customer")]
    public async Task<IActionResult> RegisterCustomer([FromBody] RegisterCustomerRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        _logger.LogInformation("Registering customer: {Email}", request.Email);

        // Pre-check if an account with this email already exists in our local database
        var existingAccount = await _repository.GetUserAccountByEmailAsync(request.Email);
        if (existingAccount != null)
        {
            return BadRequest(new { message = $"An account with the email '{request.Email}' already exists in the system." });
        }

        try
        {
            // 1. Create user in WSO2 IS via SCIM 2.0 with isapproved = "approved" (auto-approved)
            string wso2UserId = await _scimClient.CreateUserAsync(
                username: request.Email,
                password: request.Password,
                email: request.Email,
                fullName: request.FullName,
                initialStatus: "approved"
            );

            // 2. Create the local UserAccount
            var localAccount = new UserAccount
            {
                Id = Guid.NewGuid(),
                Wso2Sub = wso2UserId,
                Email = request.Email,
                FullName = request.FullName,
                Role = "Customer",
                ApprovalStatus = "approved",
                CreatedAt = DateTime.UtcNow
            };

            await _repository.CreateUserAccountAsync(localAccount);

            return Ok(new { message = "Registration successful. You can now log in.", wso2UserId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register customer.");
            
            // Extract clean SCIM error message details if returned by Asgardeo
            var message = ex.Message;
            if (message.Contains("Failed to create user in identity provider:"))
            {
                var jsonPart = message.Replace("Failed to create user in identity provider:", "").Trim();
                try
                {
                    using var doc = JsonDocument.Parse(jsonPart);
                    if (doc.RootElement.TryGetProperty("detail", out var detailProp))
                    {
                        var scimError = detailProp.GetString();
                        if (!string.IsNullOrEmpty(scimError))
                        {
                            if (scimError.Contains("already exists"))
                            {
                                return BadRequest(new { message = $"An account with the email '{request.Email}' already exists in the system." });
                            }
                            return BadRequest(new { message = scimError });
                        }
                    }
                }
                catch
                {
                    // Fallback
                }
            }

            return Problem(
                detail: "An error occurred during registration. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Registration failed");
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

public record RegisterCustomerRequest(
    string FullName,
    string Email,
    string Password
);
