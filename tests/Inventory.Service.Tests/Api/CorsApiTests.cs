using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace Inventory.Service.Tests.Api;

// CORS for the public availability endpoint (SCRUM-8 follow-up): the
// frontend origin must receive Access-Control-Allow-Origin so the browser
// does not block the response.
public class CorsApiTests : IClassFixture<InventoryApiFactory>
{
    private const string AllowedOrigin = "http://localhost:5173";

    private readonly InventoryApiFactory _factory;

    public CorsApiTests(InventoryApiFactory factory)
    {
        _factory = factory;
        _factory.ResetRepositoryDefaults();
    }

    [Fact]
    public async Task Availability_AllowedOrigin_ReturnsAccessControlAllowOriginHeader()
    {
        var showId = Guid.NewGuid();
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/inventory/shows/{showId}/availability");
        request.Headers.Add("Origin", AllowedOrigin);

        var response = await client.SendAsync(request);

        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(AllowedOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").First());
    }
}
