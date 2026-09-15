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
/// Stands in for the real "Internal" JWT scheme used by machine-to-machine
/// callers. Authenticates using the subject in <see cref="SubHeaderName"/>,
/// and carries whatever scopes are in <see cref="ScopeHeaderName"/> as
/// individual "scope" claims — mirroring the OnTokenValidated splitting
/// Program.cs applies to a real token's space-separated scope claim — plus
/// an "aut" claim from <see cref="AutHeaderName"/> when present.
/// </summary>
public class InternalTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Internal";
    public const string SubHeaderName = "Test-Internal-Sub";
    public const string ScopeHeaderName = "Test-Internal-Scope";
    public const string AutHeaderName = "Test-Internal-Aut";

    public InternalTestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
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

        if (Request.Headers.TryGetValue(ScopeHeaderName, out var scopeValues) && !string.IsNullOrEmpty(scopeValues))
        {
            foreach (var scope in scopeValues.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                claims.Add(new Claim("scope", scope));
            }
        }

        if (Request.Headers.TryGetValue(AutHeaderName, out var autValues) && !string.IsNullOrEmpty(autValues))
        {
            claims.Add(new Claim("aut", autValues.ToString()));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
