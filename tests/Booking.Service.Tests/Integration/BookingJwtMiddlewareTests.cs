using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using Jwt = Booking.Service.Tests.Integration.BookingJwtTestSupport;

namespace Booking.Service.Tests.Integration;

// Booking's real JWT middleware with locally signed tokens: audiences, token type, signature,
// issuer, lifetime and the split between the user and internal schemes.
[Collection("Postgres")]
public sealed class BookingJwtMiddlewareTests : IAsyncLifetime
{
    private readonly PostgresFixture _db;
    private TicketValidationFactory _factory = null!;

    public BookingJwtMiddlewareTests(PostgresFixture db) => _db = db;

    public Task InitializeAsync()
    {
        _factory = new TicketValidationFactory(_db.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static readonly string[] Admin = { "Admin" };

    private async Task<HttpStatusCode> ValidateAsync(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/booking/tickets/{Guid.CreateVersion7()}/validate");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await _factory.CreateClient().SendAsync(request)).StatusCode;
    }

    private async Task<HttpStatusCode> InternalAsync(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/internal/booking/cancellations/shows/{Guid.CreateVersion7()}");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await _factory.CreateClient().SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task UserScheme_ValidAdminToken_IsAuthenticated()
    {
        // 404: past authentication and the role gate, the ticket simply does not exist.
        Assert.Equal(HttpStatusCode.NotFound, await ValidateAsync(Jwt.Mint(Jwt.UserAudience, groups: Admin, aut: "APPLICATION_USER")));
    }

    [Fact]
    public async Task UserScheme_NoToken_Returns401() => Assert.Equal(HttpStatusCode.Unauthorized, await ValidateAsync(null));

    [Theory]
    [InlineData("unknown-client")]
    [InlineData("test-spa-client-2")]
    [InlineData("")]
    public async Task UserScheme_UnlistedAudience_Returns401(string audience) =>
        Assert.Equal(HttpStatusCode.Unauthorized, await ValidateAsync(Jwt.Mint(audience, groups: Admin)));

    [Fact]
    public async Task UserScheme_MachineTokenAudience_Returns401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await ValidateAsync(Jwt.Mint(Jwt.InternalAudience, groups: Admin, aut: "APPLICATION")));

    [Theory]
    [InlineData("JWT")]
    [InlineData("id_token")]
    [InlineData(null)]
    public async Task UserScheme_IdTokenOrUntypedToken_Returns401(string? type) =>
        Assert.Equal(HttpStatusCode.Unauthorized, await ValidateAsync(Jwt.Mint(Jwt.UserAudience, groups: Admin, type: type)));

    [Fact]
    public async Task UserScheme_WrongIssuer_Returns401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await ValidateAsync(Jwt.Mint(Jwt.UserAudience, groups: Admin, issuer: "https://evil.test/token")));

    [Fact]
    public async Task UserScheme_ExpiredToken_Returns401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await ValidateAsync(Jwt.Mint(Jwt.UserAudience, groups: Admin, expires: DateTime.UtcNow.AddMinutes(-5))));

    [Fact]
    public async Task UserScheme_ForeignSigningKey_Returns401()
    {
        var foreign = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "booking-test-key" };
        Assert.Equal(HttpStatusCode.Unauthorized, await ValidateAsync(Jwt.Mint(Jwt.UserAudience, groups: Admin, key: foreign)));
    }

    [Fact]
    public async Task UserScheme_CustomerToken_StillForbiddenFromValidation() =>
        Assert.Equal(HttpStatusCode.Forbidden, await ValidateAsync(Jwt.Mint(Jwt.UserAudience, groups: new[] { "Customer" })));

    [Fact]
    public async Task InternalScheme_MachineTokenWithScope_IsAccepted() =>
        Assert.Equal(HttpStatusCode.NoContent, await InternalAsync(Jwt.Mint(Jwt.InternalAudience, aut: "APPLICATION", scope: "openid booking:write")));

    [Fact]
    public async Task InternalScheme_MachineTokenWithoutRequiredScope_Returns403() =>
        Assert.Equal(HttpStatusCode.Forbidden, await InternalAsync(Jwt.Mint(Jwt.InternalAudience, aut: "APPLICATION", scope: "catalog:read")));

    [Fact]
    public async Task InternalScheme_UserTokenAudience_Returns401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await InternalAsync(Jwt.Mint(Jwt.UserAudience, aut: "APPLICATION_USER", scope: "booking:write", groups: Admin)));

    [Fact]
    public async Task InternalScheme_UnlistedAudience_Returns401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await InternalAsync(Jwt.Mint("other-m2m-client", aut: "APPLICATION", scope: "booking:write")));

    [Fact]
    public async Task InternalScheme_IdToken_Returns401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await InternalAsync(Jwt.Mint(Jwt.InternalAudience, aut: "APPLICATION", scope: "booking:write", type: "JWT")));

    [Fact]
    public async Task InternalScheme_NoToken_Returns401() => Assert.Equal(HttpStatusCode.Unauthorized, await InternalAsync(null));

    [Fact]
    public void Startup_WithoutInternalAudiences_Fails()
    {
        using var factory = new TicketValidationFactory(_db.ConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:InternalAudiences:0"] = "" })));

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Jwt:InternalAudiences", ex.ToString());
    }
}
