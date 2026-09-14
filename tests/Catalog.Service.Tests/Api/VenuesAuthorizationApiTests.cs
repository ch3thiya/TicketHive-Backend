using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Moq;
using Xunit;
using Catalog.Service.Models;

namespace Catalog.Service.Tests.Api;

public class VenuesAuthorizationApiTests : IClassFixture<CatalogApiFactory>
{
    private readonly CatalogApiFactory _factory;

    public VenuesAuthorizationApiTests(CatalogApiFactory factory)
    {
        _factory = factory;
        _factory.ResetRepositoryDefaults();
    }

    private static void AddAuth(HttpRequestMessage request, string sub, string? role = null)
    {
        request.Headers.Add(TestAuthHandler.SubHeaderName, sub);
        if (role != null)
        {
            request.Headers.Add(TestAuthHandler.RoleHeaderName, role);
        }
    }

    private static object VenueBody() => new { name = "Nelum Pokuna", address = "Colombo 07", capacity = 500 };

    // GET /api/catalog/venues

    [Fact]
    public async Task GetVenues_NoToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/catalog/venues");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetVenues_AuthenticatedNonAdmin_ReturnsOk()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/venues");
        AddAuth(request, "customer-sub", role: "Customer");
        var client = _factory.CreateClient();

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // GET /api/catalog/venues/{id}

    [Fact]
    public async Task GetVenueById_NoToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/catalog/venues/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetVenueById_UnknownId_ReturnsNotFound()
    {
        var id = Guid.NewGuid();
        _factory.VenueRepositoryMock.Setup(r => r.GetVenueByIdAsync(id)).ReturnsAsync((Venue?)null);
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/catalog/venues/{id}");
        AddAuth(request, "customer-sub", role: "Customer");
        var client = _factory.CreateClient();

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetVenueById_AuthenticatedNonAdmin_KnownId_ReturnsOk()
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        _factory.VenueRepositoryMock.Setup(r => r.GetVenueByIdAsync(id)).ReturnsAsync(new Venue
        {
            Id = id,
            Name = "Nelum Pokuna",
            Address = "Colombo 07",
            Capacity = 500,
            CreatedAt = now,
            UpdatedAt = now
        });
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/catalog/venues/{id}");
        AddAuth(request, "customer-sub", role: "Customer");
        var client = _factory.CreateClient();

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // POST /api/catalog/venues

    [Fact]
    public async Task CreateVenue_NoToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/catalog/venues", VenueBody());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateVenue_AuthenticatedNonAdmin_ReturnsForbidden()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/venues")
        {
            Content = JsonContent.Create(VenueBody())
        };
        AddAuth(request, "customer-sub", role: "Customer");
        var client = _factory.CreateClient();

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateVenue_Admin_ReturnsCreated()
    {
        _factory.VenueRepositoryMock
            .Setup(r => r.CreateVenueAsync(It.IsAny<Venue>()))
            .ReturnsAsync((Venue v) => v);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/venues")
        {
            Content = JsonContent.Create(VenueBody())
        };
        AddAuth(request, "admin-sub", role: "Admin");
        var client = _factory.CreateClient();

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // PUT /api/catalog/venues/{id}

    [Fact]
    public async Task UpdateVenue_NoToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync($"/api/catalog/venues/{Guid.NewGuid()}", VenueBody());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdateVenue_AuthenticatedNonAdmin_ReturnsForbidden()
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/catalog/venues/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(VenueBody())
        };
        AddAuth(request, "customer-sub", role: "Customer");
        var client = _factory.CreateClient();

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // DELETE /api/catalog/venues/{id}

    [Fact]
    public async Task DeleteVenue_NoToken_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.DeleteAsync($"/api/catalog/venues/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteVenue_AuthenticatedNonAdmin_ReturnsForbidden()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/catalog/venues/{Guid.NewGuid()}");
        AddAuth(request, "customer-sub", role: "Customer");
        var client = _factory.CreateClient();

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
