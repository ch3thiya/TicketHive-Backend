using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
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

// The real Catalog pipeline and its Internal JWT scheme with locally signed tokens: only a machine
// token with catalog:read may ask for reminder details.
public sealed class ReminderInfoApiTests : IClassFixture<CatalogApiFactory>
{
    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "catalog-reminder-key" };
    private const string Path = "/internal/catalog/shows/reminder-info";
    private readonly CatalogApiFactory _factory;
    private readonly string _issuer;

    public ReminderInfoApiTests(CatalogApiFactory factory)
    {
        _factory = factory;
        factory.ResetRepositoryDefaults();
        _issuer = factory.Services.GetRequiredService<IConfiguration>()["Jwt:Authority"]!;
    }

    private HttpClient Client() => _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = _issuer };
        configuration.SigningKeys.Add(SigningKey);
        services.PostConfigure<JwtBearerOptions>("Internal", options =>
        {
            options.Configuration = configuration;
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
        });
    })).CreateClient();

    private string Mint(string? aut, string? scope) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = _issuer,
        Claims = new Dictionary<string, object>
        {
            ["aud"] = "notification-m2m-client",
            ["sub"] = "notification",
            ["aut"] = aut ?? string.Empty,
            ["scope"] = scope ?? string.Empty
        },
        NotBefore = DateTime.UtcNow.AddMinutes(-10),
        IssuedAt = DateTime.UtcNow.AddMinutes(-10),
        Expires = DateTime.UtcNow.AddMinutes(30),
        TokenType = "at+jwt",
        SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256)
    });

    private Task<HttpResponseMessage> PostAsync(object body, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = JsonContent.Create(body) };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return Client().SendAsync(request);
    }

    private static object Ids(params Guid[] ids) => new { showIds = ids };

    [Fact]
    public async Task AMachineTokenWithCatalogRead_GetsTheReminderDetails()
    {
        var show = Guid.NewGuid();
        _factory.EventRepositoryMock.Setup(r => r.GetShowReminderInfoAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == show)))
            .ReturnsAsync(new List<ShowReminderInfo> { new(show, new DateOnly(2026, 12, 1), new TimeOnly(19, 0), "Active", "Arijit Singh Live", "Published", "Nelum Pokuna") });

        var response = await PostAsync(Ids(show, show), Mint("APPLICATION", "openid catalog:read"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("shows")[0];
        Assert.Equal("Arijit Singh Live", item.GetProperty("eventName").GetString());
        Assert.Equal("Nelum Pokuna", item.GetProperty("venueName").GetString());
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 13, 30, 0, TimeSpan.Zero), item.GetProperty("startsAtUtc").GetDateTimeOffset());
        Assert.False(item.GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public async Task NoToken_Returns401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(Ids(Guid.NewGuid()), null)).StatusCode);

    [Theory]
    [InlineData("APPLICATION", "inventory:write")]
    [InlineData("APPLICATION", "catalog:readonly")]
    [InlineData("APPLICATION_USER", "catalog:read")]
    [InlineData(null, "catalog:read")]
    public async Task TheWrongScopeOrTokenType_IsRefusedAndNothingIsRead(string? aut, string scope)
    {
        var response = await PostAsync(Ids(Guid.NewGuid()), Mint(aut, scope));

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
        _factory.EventRepositoryMock.Verify(r => r.GetShowReminderInfoAsync(It.IsAny<IReadOnlyCollection<Guid>>()), Times.Never);
    }

    [Fact]
    public async Task AnEmptyOrOversizedRequest_Returns400()
    {
        var token = Mint("APPLICATION", "catalog:read");

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(new { showIds = Array.Empty<Guid>() }, token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(new { showIds = Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()) }, token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(new { showIds = new[] { Guid.Empty } }, token)).StatusCode);
    }
}
