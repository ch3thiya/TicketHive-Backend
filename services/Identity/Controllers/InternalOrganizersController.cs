using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Identity.Service.Db;

namespace Identity.Service.Controllers;

public record OrganizerLookupResponse(Guid OrganizerId);

// TODO: require an Asgardeo client-credentials (machine-to-machine) token once
// feature/internal-service-auth lands. Left open in Development until then.
[ApiController]
[Route("internal/identity/organizers")]
[AllowAnonymous]
public class InternalOrganizersController : ControllerBase
{
    private readonly IAccountRepository _repository;

    public InternalOrganizersController(IAccountRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// Reports whether the given Asgardeo subject is an approved, active organizer.
    /// Returns 404 for any user who is not, including pending and rejected applicants.
    /// </summary>
    [HttpGet("{sub}")]
    [ProducesResponseType(typeof(OrganizerLookupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrganizerBySub(string sub)
    {
        var account = await _repository.GetUserAccountBySubAsync(sub);
        if (account == null || account.Role != "Organizer" || account.ApprovalStatus != "approved")
        {
            return NotFound();
        }

        return Ok(new OrganizerLookupResponse(account.Id));
    }
}
