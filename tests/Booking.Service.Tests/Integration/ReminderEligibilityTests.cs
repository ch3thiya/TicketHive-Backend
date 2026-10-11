using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Booking.Service.Db;
using Booking.Service.Models;
using Xunit;

namespace Booking.Service.Tests.Integration;

// Which orders may still be reminded: confirmed, with live tickets. Real PostgreSQL, real JWTs.
[Collection("Postgres")]
public sealed class ReminderEligibilityTests : IAsyncLifetime
{
    private readonly PostgresFixture _db;
    private readonly SalesTestData _data;
    private TicketValidationFactory _factory = null!;

    public ReminderEligibilityTests(PostgresFixture db)
    {
        _db = db;
        _data = new SalesTestData(db.ConnectionString);
    }

    public Task InitializeAsync()
    {
        _factory = new TicketValidationFactory(_db.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static readonly Guid Category = Guid.CreateVersion7();

    private ReminderEligibilityRepository Repository() => new(_data.Connections());

    private async Task<Order> IssuedOrderAsync(OrderStatus status, int tickets = 2)
    {
        // The database only allows tickets on a confirmed order, so the order changes status afterwards.
        var order = await _data.AddOrderAsync(Guid.CreateVersion7(), OrderStatus.Confirmed, "LKR", (Category, tickets, 50m));
        await _data.IssueTicketsAsync(order);
        if (status != OrderStatus.Confirmed) await _data.SetOrderStatusAsync(order.Id, status);
        return order;
    }

    // ---- repository ----

    [Fact]
    public async Task AConfirmedOrder_IsEligibleWithItsLiveTicketCount()
    {
        var order = await IssuedOrderAsync(OrderStatus.Confirmed, 3);

        var eligible = Assert.Single(await Repository().GetEligibleAsync(new[] { order.Id }));

        Assert.Equal((order.Id, 3), (eligible.OrderId, eligible.TicketCount));
    }

    [Theory]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Failed)]
    [InlineData(OrderStatus.PaymentPending)]
    public async Task OrdersThatAreNotConfirmed_AreLeftOut(OrderStatus status)
    {
        var order = await IssuedOrderAsync(status);

        Assert.Empty(await Repository().GetEligibleAsync(new[] { order.Id }));
    }

    [Fact]
    public async Task VoidedTicketsAreNotCounted_AndAnOrderWithNoneLeftIsLeftOut()
    {
        var order = await _data.AddOrderAsync(Guid.CreateVersion7(), OrderStatus.Confirmed, "LKR", (Category, 2, 50m));
        var tickets = await _data.IssueTicketsAsync(order);
        await _data.VoidAsync(tickets[0]);
        Assert.Equal(1, Assert.Single(await Repository().GetEligibleAsync(new[] { order.Id })).TicketCount);

        await _data.VoidAsync(tickets[1]);

        Assert.Empty(await Repository().GetEligibleAsync(new[] { order.Id }));
    }

    [Fact]
    public async Task AConfirmedOrderWhoseTicketsAreNotIssuedYet_IsNotEligibleYet()
    {
        var order = await _data.AddOrderAsync(Guid.CreateVersion7(), OrderStatus.Confirmed, "LKR", (Category, 1, 50m));

        Assert.Empty(await Repository().GetEligibleAsync(new[] { order.Id }));
    }

    [Fact]
    public async Task ManyOrders_AreAnsweredTogether_AndUnknownIdsAreLeftOut()
    {
        var a = await IssuedOrderAsync(OrderStatus.Confirmed, 1);
        var b = await IssuedOrderAsync(OrderStatus.Confirmed, 2);
        var cancelled = await IssuedOrderAsync(OrderStatus.Cancelled);

        var eligible = await Repository().GetEligibleAsync(new[] { a.Id, b.Id, cancelled.Id, Guid.NewGuid() });

        Assert.Equal(new[] { (a.Id, 1), (b.Id, 2) }.OrderBy(x => x.Item1), eligible.Select(e => (e.OrderId, e.TicketCount)).OrderBy(x => x.Item1));
    }

    [Fact]
    public async Task NoIds_ReturnsNothing() => Assert.Empty(await Repository().GetEligibleAsync(Array.Empty<Guid>()));

    // ---- endpoint, with locally signed tokens ----

    private Task<HttpResponseMessage> PostAsync(object body, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/internal/booking/orders/reminder-eligibility") { Content = JsonContent.Create(body) };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _factory.CreateClient().SendAsync(request);
    }

    private static string MachineToken(string scope, string aut = "APPLICATION") =>
        BookingJwtTestSupport.Mint(BookingJwtTestSupport.InternalAudience, aut: aut, scope: scope);

    [Fact]
    public async Task AMachineTokenWithBookingRead_GetsTheEligibleOrders()
    {
        var order = await IssuedOrderAsync(OrderStatus.Confirmed, 2);
        var cancelled = await IssuedOrderAsync(OrderStatus.Cancelled);

        var response = await PostAsync(new { orderIds = new[] { order.Id, cancelled.Id } }, MachineToken("openid booking:read"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var orders = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orders");
        Assert.Equal(1, orders.GetArrayLength());
        Assert.Equal(order.Id, orders[0].GetProperty("orderId").GetGuid());
        Assert.Equal(2, orders[0].GetProperty("ticketCount").GetInt32());
    }

    [Fact]
    public async Task NoToken_Returns401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(new { orderIds = new[] { Guid.NewGuid() } }, null)).StatusCode);

    [Theory]
    [InlineData("booking:write", "APPLICATION")]
    [InlineData("catalog:read", "APPLICATION")]
    [InlineData("booking:read", "APPLICATION_USER")]
    public async Task TheWrongScopeOrTokenType_Returns403(string scope, string aut) =>
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(new { orderIds = new[] { Guid.NewGuid() } }, MachineToken(scope, aut))).StatusCode);

    [Fact]
    public async Task AUserToken_IsNeverAccepted()
    {
        var userToken = BookingJwtTestSupport.Mint(BookingJwtTestSupport.UserAudience, groups: new[] { "Organizer" }, scope: "booking:read", aut: "APPLICATION");

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(new { orderIds = new[] { Guid.NewGuid() } }, userToken)).StatusCode);
    }

    [Fact]
    public async Task AnEmptyOrOversizedRequest_Returns400()
    {
        var token = MachineToken("booking:read");

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(new { orderIds = Array.Empty<Guid>() }, token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(new { orderIds = Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()) }, token)).StatusCode);
    }
}
