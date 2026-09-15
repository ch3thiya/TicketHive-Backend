using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inventory.Service.Tests.Api;

/// <summary>
/// Stands in for the real customer-facing JWT scheme. Authenticates using
/// the subject in <see cref="SubHeaderName"/>, and maps whatever values are
/// carried in <see cref="GroupsHeaderName"/> onto "groups" claims with
/// "groups" as the role claim type — the same mapping Program.cs configures
/// via RoleClaimType for real Asgardeo tokens — so IsInRole works exactly as
/// it would for the real scheme.
/// </summary>
public class CustomerTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "TestCustomerScheme";
    public const string SubHeaderName = "Test-Sub";
    public const string GroupsHeaderName = "Test-Groups";

    public CustomerTestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(SubHeaderName, out var subValues) || string.IsNullOrEmpty(subValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subValues.ToString()) };
        if (Request.Headers.TryGetValue(GroupsHeaderName, out var groupsValues) && !string.IsNullOrEmpty(groupsValues))
        {
            foreach (var group in groupsValues.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                claims.Add(new Claim("groups", group.Trim()));
            }
        }

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.NameIdentifier, "groups");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
