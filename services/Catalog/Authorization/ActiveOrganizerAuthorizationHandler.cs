using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Catalog.Service.Clients;

namespace Catalog.Service.Authorization;

public class ActiveOrganizerAuthorizationHandler : AuthorizationHandler<ActiveOrganizerRequirement>
{
    public const string OrganizerIdItemKey = "ActiveOrganizerId";
    public const string IdentityUnavailableItemKey = "ActiveOrganizerIdentityUnavailable";

    private readonly IOrganizerStatusClient _organizerStatusClient;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ActiveOrganizerAuthorizationHandler> _logger;

    public ActiveOrganizerAuthorizationHandler(
        IOrganizerStatusClient organizerStatusClient,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ActiveOrganizerAuthorizationHandler> logger)
    {
        _organizerStatusClient = organizerStatusClient;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveOrganizerRequirement requirement)
    {
        var sub = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst("sub")?.Value;

        if (string.IsNullOrEmpty(sub))
        {
            context.Fail();
            return;
        }

        var httpContext = _httpContextAccessor.HttpContext;
        var result = await _organizerStatusClient.GetOrganizerStatusAsync(sub, httpContext?.RequestAborted ?? CancellationToken.None);

        switch (result.Status)
        {
            case OrganizerLookupStatus.Active:
                if (httpContext is not null)
                {
                    httpContext.Items[OrganizerIdItemKey] = result.OrganizerId;
                }
                context.Succeed(requirement);
                break;

            case OrganizerLookupStatus.Unavailable:
                _logger.LogWarning("Organizer status lookup was unavailable; denying the write request");
                if (httpContext is not null)
                {
                    httpContext.Items[IdentityUnavailableItemKey] = true;
                }
                context.Fail();
                break;

            case OrganizerLookupStatus.NotFound:
            default:
                context.Fail();
                break;
        }
    }
}
