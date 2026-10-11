using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Catalog.Service.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;

namespace Catalog.Service.Tests.Api;

// Catalog's real JWT schemes with locally signed tokens: the cancellation rules endpoint is called
// by Booking with a machine token, which only the Internal scheme accepts.
public sealed class CancellationInternalJwtTests : IClassFixture<CatalogApiFactory>
{
    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "catalog-test-key" };
    private readonly CatalogApiFactory _factory;
    private readonly Guid _showId = Guid.NewGuid();
    private readonly string _issuer;
    private readonly string _userAudience;

    public CancellationInternalJwtTests(CatalogApiFactory factory)
    {
        _factory = factory;
        factory.ResetRepositoryDefaults();
        factory.EventRepositoryMock.Setup(r => r.GetShowByIdAsync(_showId)).ReturnsAsync(new Show
        {
            Id = _showId,
            EventId = Guid.NewGuid(),
            Status = "Published",
            ShowDate = new DateOnly(2027, 1, 1),
            ShowTime = new TimeOnly(18, 0)
        });
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        _issuer = configuration["Jwt:Authority"]!;
        _userAudience = configuration["Jwt:Audience"]!;
    }

    private HttpClient Client() => _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = _issuer };
        configuration.SigningKeys.Add(SigningKey);
        foreach (var scheme in new[] { "Bearer", "Internal" })
        {
            services.PostConfigure<JwtBearerOptions>(scheme, options =>
            {
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            });
        }
        // The shared factory fakes authentication; these tests need the real default scheme back.
        services.PostConfigure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(o =>
            o.DefaultAuthenticateScheme = o.DefaultChallengeScheme = o.DefaultScheme = "Bearer");
    })).CreateClient();

    private string Mint(string audience, string? aut, string? scope, string[]? groups = null, string? issuer = null, DateTime? expires = null, SecurityKey? key = null)
    {
        var claims = new Dictionary<string, object> { ["aud"] = audience, ["sub"] = "caller" };
        if (aut is not null) claims["aut"] = aut;
        if (scope is not null) claims["scope"] = scope;
        if (groups is not null) claims["groups"] = groups;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? _issuer,
            Claims = claims,
            NotBefore = DateTime.UtcNow.AddMinutes(-10),
            IssuedAt = DateTime.UtcNow.AddMinutes(-10),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(30),
            TokenType = "at+jwt",
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.RsaSha256)
        });
    }

    private async Task<HttpStatusCode> GetAsync(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await Client().SendAsync(request)).StatusCode;
    }

    private Task<HttpStatusCode> RulesAsync(string? token) => GetAsync($"/internal/catalog/cancellations/shows/{_showId}/rules", token);

    // A machine token's audience is the calling app's own client ID, not the SPA's.
    private const string MachineAudience = "booking-m2m-client";

    [Fact]
    public async Task Rules_BookingMachineToken_IsAccepted() =>
        Assert.Equal(HttpStatusCode.OK, await RulesAsync(Mint(MachineAudience, "APPLICATION", "openid catalog:read")));

    [Fact]
    public async Task Rules_NoToken_Returns401() => Assert.Equal(HttpStatusCode.Unauthorized, await RulesAsync(null));

    [Fact]
    public async Task Rules_UserToken_Returns403() =>
        Assert.Equal(HttpStatusCode.Forbidden, await RulesAsync(Mint(_userAudience, "APPLICATION_USER", "catalog:read", new[] { "Admin" })));

    [Theory]
    [InlineData("inventory:write")]
    [InlineData("catalog:readonly")]
    [InlineData("")]
    public async Task Rules_MachineTokenWithoutCatalogReadScope_Returns403(string scope) =>
        Assert.Equal(HttpStatusCode.Forbidden, await RulesAsync(Mint(MachineAudience, "APPLICATION", scope)));

    [Fact]
    public async Task Rules_WrongIssuer_Returns401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await RulesAsync(Mint(MachineAudience, "APPLICATION", "catalog:read", issuer: "https://evil.test/token")));

    [Fact]
    public async Task Rules_ExpiredToken_Returns401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await RulesAsync(Mint(MachineAudience, "APPLICATION", "catalog:read", expires: DateTime.UtcNow.AddMinutes(-5))));

    [Fact]
    public async Task Rules_ForeignSigningKey_Returns401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await RulesAsync(Mint(MachineAudience, "APPLICATION", "catalog:read", key: new RsaSecurityKey(RSA.Create(2048)) { KeyId = "catalog-test-key" })));

    [Fact]
    public async Task UserEndpoint_MachineTokenAudience_StillReturns401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync($"/api/catalog/shows/{_showId}/cancellation", Mint(MachineAudience, "APPLICATION", "catalog:read")));
}