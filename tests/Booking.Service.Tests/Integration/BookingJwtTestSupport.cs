using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Booking.Service.Tests.Integration;

/// <summary>
/// Real JWT bearer validation for Booking tests: tokens are signed with a throwaway local key
/// and the schemes trust only that key. Nothing contacts Asgardeo.
/// </summary>
internal static class BookingJwtTestSupport
{
    public const string Authority = "https://issuer.test/oauth2/token";
    public const string UserAudience = "test-spa-client";
    public const string InternalAudience = "test-catalog-m2m-client";

    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "booking-test-key" };

    public static string Mint(
        string audience, string? sub = "user", string? scope = null, string? aut = null, string[]? groups = null,
        string? type = "at+jwt", string? issuer = Authority, DateTime? expires = null, SecurityKey? key = null)
    {
        var claims = new Dictionary<string, object> { ["aud"] = audience };
        if (sub is not null) claims["sub"] = sub;
        if (scope is not null) claims["scope"] = scope;
        if (aut is not null) claims["aut"] = aut;
        if (groups is not null) claims["groups"] = groups;
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Claims = claims,
            NotBefore = now.AddMinutes(-10),
            IssuedAt = now.AddMinutes(-10),
            Expires = expires ?? now.AddMinutes(30),
            TokenType = type,
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.RsaSha256)
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public static void ConfigureApp(IConfigurationBuilder config) => config.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Jwt:Authority"] = Authority,
        ["Jwt:Audience"] = UserAudience,
        ["Jwt:InternalAudiences:0"] = InternalAudience
    });

    /// <summary>Makes both Booking schemes trust the local key instead of fetching Asgardeo metadata.</summary>
    public static void TrustLocalKey(IServiceCollection services)
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = Authority };
        configuration.SigningKeys.Add(SigningKey);
        foreach (var scheme in new[] { "Bearer", "Internal" })
        {
            services.PostConfigure<JwtBearerOptions>(scheme, options =>
            {
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            });
        }
    }

    /// <summary>
    /// Lets tests keep describing the caller with Test-* headers while the request still goes
    /// through the real JWT middleware: the headers are turned into a signed bearer token.
    /// Test-Scope/Test-Aut mark a machine token (internal audience); otherwise a user token.
    /// </summary>
    public static void MintFromTestHeaders(IServiceCollection services) =>
        services.AddSingleton<IStartupFilter>(new HeaderMintingStartupFilter());

    private sealed class HeaderMintingStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, pipeline) =>
            {
                var headers = context.Request.Headers;
                if (headers.TryGetValue("Test-Sub", out var sub))
                {
                    var machine = headers.ContainsKey("Test-Scope") || headers.ContainsKey("Test-Aut");
                    var role = headers.TryGetValue("Test-Role", out var r) && !string.IsNullOrEmpty(r) ? new[] { r.ToString() } : null;
                    var token = Mint(
                        machine ? InternalAudience : UserAudience,
                        sub.ToString(),
                        headers.TryGetValue("Test-Scope", out var scope) ? scope.ToString() : null,
                        headers.TryGetValue("Test-Aut", out var aut) ? aut.ToString() : null,
                        role);
                    context.Request.Headers.Authorization = $"Bearer {token}";
                }
                await pipeline();
            });
            next(app);
        };
    }
}
