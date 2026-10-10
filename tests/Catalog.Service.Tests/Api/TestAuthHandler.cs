using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Catalog.Service.Tests.Api;

/// <summary>
/// Stands in for real JWT validation in API tests. Authenticates the caller
/// using the subject carried in the <see cref="SubHeaderName"/> header, and
/// grants whatever role is carried in <see cref="RoleHeaderName"/>, so tests
/// can drive 401 (header absent), 403 (wrong/missing role) and 200 (matching
/// role) without needing a real Asgardeo token.
/// </summary>
public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "TestScheme";
    public const string SubHeaderName = "Test-Sub";
    public const string RoleHeaderName = "Test-Role";
    public const string ScopeHeaderName = "Test-Scope";
    public const string AuthenticationTypeHeaderName = "Test-Aut";

    public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
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
        if (Request.Headers.TryGetValue(RoleHeaderName, out var roleValues) && !string.IsNullOrEmpty(roleValues))
        {
            claims.Add(new Claim(ClaimTypes.Role, roleValues.ToString()));
        }
        if (Request.Headers.TryGetValue(ScopeHeaderName, out var scopeValues)) claims.Add(new Claim("scope", scopeValues.ToString()));
        if (Request.Headers.TryGetValue(AuthenticationTypeHeaderName, out var autValues)) claims.Add(new Claim("aut", autValues.ToString()));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
