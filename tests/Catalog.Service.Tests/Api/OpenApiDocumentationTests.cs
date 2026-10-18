using System.Net;
using System.Text.Json;
using Xunit;

namespace Catalog.Service.Tests.Api;

public class OpenApiDocumentationTests : IClassFixture<CatalogApiFactory>
{
    private readonly CatalogApiFactory _factory;

    public OpenApiDocumentationTests(CatalogApiFactory factory)
    {
        _factory = factory;
        _factory.ResetRepositoryDefaults();
    }

    [Fact]
    public async Task Document_is_public_and_describes_bearer_security()
    {
        var response = await _factory.CreateClient().GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        var server = root.GetProperty("servers")[0];
        Assert.Equal("/", server.GetProperty("url").GetString());
        Assert.Equal("TicketHive API Gateway", server.GetProperty("description").GetString());

        var bearer = root.GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("Bearer");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());

        var protectedGet = root.GetProperty("paths")
            .GetProperty("/api/catalog/venues")
            .GetProperty("get");
        Assert.True(protectedGet.GetProperty("security").GetArrayLength() > 0);

        var publicGet = root.GetProperty("paths")
            .GetProperty("/api/catalog/events")
            .GetProperty("get");
        Assert.False(publicGet.TryGetProperty("security", out var security) && security.GetArrayLength() > 0);
    }
}
