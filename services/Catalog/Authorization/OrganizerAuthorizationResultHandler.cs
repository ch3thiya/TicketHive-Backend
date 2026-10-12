using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Catalog.Service.Authorization;

/// <summary>
/// Wraps the default authorization result handling to turn a failure caused by
/// Identity being unreachable into 503, instead of the usual 403.
/// </summary>
public class OrganizerAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    public const string SuspendedProblemCode = "OrganizerSuspended";

    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Succeeded && context.Items.TryGetValue(ActiveOrganizerAuthorizationHandler.SuspendedItemKey, out var suspended) && suspended is true)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/problem+json";
            var suspendedProblem = new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Organizer suspended",
                Detail = "This organizer account is suspended. Event and show management is unavailable until an administrator reinstates it."
            };
            suspendedProblem.Extensions["code"] = SuspendedProblemCode;
            await context.Response.WriteAsJsonAsync(suspendedProblem, options: null, contentType: "application/problem+json");
            return;
        }
        if (!authorizeResult.Succeeded &&
            context.Items.TryGetValue(ActiveOrganizerAuthorizationHandler.IdentityUnavailableItemKey, out var unavailable) &&
            unavailable is true)
        {
            var problemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Organizer status unavailable",
                Detail = "Could not verify organizer status right now. Please try again shortly."
            };

            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(problemDetails, options: null, contentType: "application/problem+json");
            return;
        }

        await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}
