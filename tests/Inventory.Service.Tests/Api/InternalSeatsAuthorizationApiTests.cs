using System.Net;
using System.Net.Http.Json;
using Inventory.Service.Models;
using Moq;
using Xunit;

namespace Inventory.Service.Tests.Api;

// The real Inventory pipeline for the internal seats endpoints: only a machine token with the
// inventory:write scope may enable a layout or read the lock state.
public class InternalSeatsAuthorizationApiTests : IClassFixture<InventoryApiFactory>
{
    private readonly InventoryApiFactory _factory;
    private readonly Guid _show = Guid.NewGuid();

    public InternalSeatsAuthorizationApiTests(InventoryApiFactory factory)
    {
        _factory = factory;
        factory.SeatLayoutRepositoryMock.Reset();
        factory.SeatLayoutRepositoryMock
            .Setup(r => r.EnableAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<SeatToInitialize>>(), It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(new EnableSeatsResult(EnableSeatsOutcome.Enabled));
        factory.SeatLayoutRepositoryMock
            .Setup(r => r.GetLockStateAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new SeatLockState(true, false, 1));
    }

    private static object Body(int version = 1) => new
    {
        version,
        seats = new[] { new { categoryId = Guid.NewGuid(), section = "Front", row = "A", number = 1 } }
    };

    private HttpRequestMessage Put(object? body = null, string? scope = "inventory:write", string? aut = "APPLICATION", bool token = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/internal/inventory/shows/{_show}/seats") { Content = JsonContent.Create(body ?? Body()) };
        Authenticate(request, scope, aut, token);
        return request;
    }

    private HttpRequestMessage Get(string? scope = "inventory:write", string? aut = "APPLICATION", bool token = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/inventory/shows/{_show}/seats/lock");
        Authenticate(request, scope, aut, token);
        return request;
    }

    private static void Authenticate(HttpRequestMessage request, string? scope, string? aut, bool token)
    {
        if (!token) return;
        request.Headers.Add(InternalTestAuthHandler.SubHeaderName, "service-client");
        if (scope is not null) request.Headers.Add(InternalTestAuthHandler.ScopeHeaderName, scope);
        if (aut is not null) request.Headers.Add(InternalTestAuthHandler.AutHeaderName, aut);
    }

    private void NothingChanged() => _factory.SeatLayoutRepositoryMock.Verify(
        r => r.EnableAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<SeatToInitialize>>(), It.IsAny<DateTimeOffset>()), Times.Never);

    [Fact]
    public async Task Enable_WithoutAToken_Returns401()
    {
        var response = await _factory.CreateClient().SendAsync(Put(token: false));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        NothingChanged();
    }

    [Theory]
    [InlineData("catalog:read", "APPLICATION")]
    [InlineData("inventory:write", "APPLICATION_USER")]
    [InlineData("inventory:write", null)]
    [InlineData(null, "APPLICATION")]
    public async Task Enable_WithTheWrongScopeOrTokenType_Returns403(string? scope, string? aut)
    {
        var response = await _factory.CreateClient().SendAsync(Put(scope: scope, aut: aut));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        NothingChanged();
    }

    [Fact]
    public async Task Enable_CustomerTokenOnTheUserScheme_IsRefused()
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/internal/inventory/shows/{_show}/seats") { Content = JsonContent.Create(Body()) };
        request.Headers.Add(CustomerTestAuthHandler.SubHeaderName, "customer");

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        NothingChanged();
    }

    [Fact]
    public async Task Enable_WithTheRightMachineToken_Returns204AndPassesTheVersionAndSeats()
    {
        var response = await _factory.CreateClient().SendAsync(Put(Body(3)));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        _factory.SeatLayoutRepositoryMock.Verify(r => r.EnableAsync(_show, 3, It.Is<IReadOnlyList<SeatToInitialize>>(s => s.Count == 1 && s[0].Section == "Front" && s[0].Row == "A"), It.IsAny<DateTimeOffset>()), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Enable_WithAnInvalidVersion_Returns400(int version)
    {
        var response = await _factory.CreateClient().SendAsync(Put(Body(version)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        NothingChanged();
    }

    [Fact]
    public async Task Enable_WithNoSeatsOrABlankSeat_Returns400()
    {
        var empty = await _factory.CreateClient().SendAsync(Put(new { version = 1, seats = Array.Empty<object>() }));
        var blank = await _factory.CreateClient().SendAsync(Put(new { version = 1, seats = new[] { new { categoryId = Guid.NewGuid(), section = " ", row = "A", number = 1 } } }));
        var zero = await _factory.CreateClient().SendAsync(Put(new { version = 1, seats = new[] { new { categoryId = Guid.NewGuid(), section = "S", row = "A", number = 0 } } }));

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        NothingChanged();
    }

    [Theory]
    [InlineData(EnableSeatsOutcome.Locked, HttpStatusCode.Conflict, "Layout locked")]
    [InlineData(EnableSeatsOutcome.StaleVersion, HttpStatusCode.Conflict, "Stale layout version")]
    [InlineData(EnableSeatsOutcome.StockMissing, HttpStatusCode.Conflict, "Show stock is not ready")]
    [InlineData(EnableSeatsOutcome.Mismatch, HttpStatusCode.BadRequest, "Invalid seat layout")]
    public async Task Enable_MapsRepositoryOutcomesToProblemDetails(EnableSeatsOutcome outcome, HttpStatusCode expected, string title)
    {
        _factory.SeatLayoutRepositoryMock
            .Setup(r => r.EnableAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<SeatToInitialize>>(), It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(new EnableSeatsResult(outcome, "detail"));

        var response = await _factory.CreateClient().SendAsync(Put());

        Assert.Equal(expected, response.StatusCode);
        Assert.Contains(title, await response.Content.ReadAsStringAsync());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Lock_RequiresTheSameMachineToken_AndReturnsTheState()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get(token: false))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Get(scope: "catalog:read"))).StatusCode);
        var ok = await client.SendAsync(Get());

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var state = await ok.Content.ReadFromJsonAsync<SeatLockState>();
        Assert.Equal(new SeatLockState(true, false, 1), state);
    }
}
