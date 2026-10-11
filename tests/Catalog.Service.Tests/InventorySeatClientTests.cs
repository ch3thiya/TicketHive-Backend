using System.Net;
using System.Text.Json;
using Catalog.Service.Clients;
using Catalog.Service.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Catalog.Service.Tests;

public class InventorySeatClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private static InventorySeatClient ClientFor(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://inventory.test/") }, NullLogger<InventorySeatClient>.Instance);

    private static readonly Guid Show = Guid.NewGuid();
    private static readonly IReadOnlyList<SeatPosition> Seats = new[] { new SeatPosition(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Front", "A", 1) };

    [Fact]
    public async Task EnableSeats_SendsTheContractInventoryExpects()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await ClientFor(handler).EnableSeatsAsync(Show, 3, Seats);

        Assert.Equal(EnableSeatsResult.Enabled, result);
        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal($"/internal/inventory/shows/{Show}/seats", handler.Uri!.AbsolutePath);
        // Inventory binds this exact shape (see its EnableSeatsRequest test).
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal(3, json.RootElement.GetProperty("version").GetInt32());
        var seat = json.RootElement.GetProperty("seats")[0];
        Assert.Equal("11111111-1111-1111-1111-111111111111", seat.GetProperty("categoryId").GetString());
        Assert.Equal("Front", seat.GetProperty("section").GetString());
        Assert.Equal("A", seat.GetProperty("row").GetString());
        Assert.Equal(1, seat.GetProperty("number").GetInt32());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "", EnableSeatsResult.Invalid)]
    [InlineData(HttpStatusCode.Conflict, "{\"title\":\"Layout locked\"}", EnableSeatsResult.Locked)]
    [InlineData(HttpStatusCode.Conflict, "{\"title\":\"Stale layout version\"}", EnableSeatsResult.Conflict)]
    [InlineData(HttpStatusCode.InternalServerError, "", EnableSeatsResult.Unavailable)]
    [InlineData(HttpStatusCode.Unauthorized, "", EnableSeatsResult.Unavailable)]
    public async Task EnableSeats_MapsInventoryResponses(HttpStatusCode status, string body, EnableSeatsResult expected)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });

        Assert.Equal(expected, await ClientFor(handler).EnableSeatsAsync(Show, 1, Seats));
    }

    [Fact]
    public async Task EnableSeats_WhenTheCallThrows_IsUnavailableNotAnException()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("down"));

        Assert.Equal(EnableSeatsResult.Unavailable, await ClientFor(handler).EnableSeatsAsync(Show, 1, Seats));
    }

    [Fact]
    public async Task GetLock_ReadsTheStateAndFailsClosedToNull()
    {
        var ok = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"seated\":true,\"locked\":true,\"version\":2}", System.Text.Encoding.UTF8, "application/json") });
        var down = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var broken = new StubHandler(_ => throw new TaskCanceledException());

        Assert.Equal(new SeatLockInfo(true, true, 2), await ClientFor(ok).GetLockAsync(Show));
        Assert.Null(await ClientFor(down).GetLockAsync(Show));
        Assert.Null(await ClientFor(broken).GetLockAsync(Show));
    }
}
