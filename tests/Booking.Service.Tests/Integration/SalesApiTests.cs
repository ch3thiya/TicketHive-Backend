using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Booking.Service.Clients;
using Booking.Service.Models;
using Xunit;

namespace Booking.Service.Tests.Integration;

// The real Booking pipeline (real JWT validation, role gate, controller) against real PostgreSQL.
// Only Catalog is replaced, by a fake whose answer can be set per caller and show.
[Collection("Postgres")]
public sealed class SalesApiTests : IAsyncLifetime
{
    private readonly PostgresFixture _db;
    private readonly SalesTestData _data;
    private TicketValidationFactory _factory = null!;

    public SalesApiTests(PostgresFixture db)
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

    private static readonly Guid Vip = Guid.CreateVersion7();
    private static readonly Guid General = Guid.CreateVersion7();

    private static HttpRequestMessage Sales(Guid show, string? sub = null, string? role = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/booking/shows/{show}/sales");
        if (sub is not null) request.Headers.Add("Test-Sub", sub);
        if (role is not null) request.Headers.Add("Test-Role", role);
        return request;
    }

    private Task<HttpResponseMessage> GetAsync(Guid show, string? sub = null, string? role = null) =>
        _factory.CreateClient().SendAsync(Sales(show, sub, role));

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private void Owns(Guid show, string sub) => _factory.EntryAccess.Set(show, sub, EntryAccessDecision.Allowed);

    [Fact]
    public async Task Anonymous_Returns401_AndAsksCatalogNothing()
    {
        var response = await GetAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, _factory.EntryAccess.Calls);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Admin")]
    [InlineData("")]
    public async Task CustomersAdminsAndGrouplessUsers_AreRefusedBeforeOwnershipIsEvenChecked(string role)
    {
        var show = Guid.CreateVersion7();
        await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 1, 120m));
        _factory.EntryAccess.Default = EntryAccessDecision.Allowed;

        var response = await GetAsync(show, "someone", role);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, _factory.EntryAccess.Calls);
        Assert.DoesNotContain("120", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnotherOrganizersShow_IsRefusedAndRevealsNoFigures()
    {
        var show = Guid.CreateVersion7();
        await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 7, 120m));
        _factory.EntryAccess.Set(show, "intruder", EntryAccessDecision.Denied);

        var response = await GetAsync(show, "intruder", "Organizer");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("ticketsSold", body);
        Assert.DoesNotContain("840", body);
    }

    [Fact]
    public async Task OwningOrganizer_SeesTheShowsFigures()
    {
        var show = Guid.CreateVersion7();
        Owns(show, "owner");
        var order = await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 2, 120m), (General, 1, 40m));
        var tickets = await _data.IssueTicketsAsync(order);
        await _data.UseAsync(tickets[0]);

        var response = await GetAsync(show, "owner", "Organizer");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(3, body.GetProperty("ticketsSold").GetInt32());
        Assert.Equal(280m, body.GetProperty("revenue").GetDecimal());
        Assert.Equal(1, body.GetProperty("ticketsUsed").GetInt32());
        Assert.Equal("LKR", body.GetProperty("currency").GetString());
        var vip = body.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("categoryId").GetGuid() == Vip);
        Assert.Equal((2, 240m), (vip.GetProperty("sold").GetInt32(), vip.GetProperty("revenue").GetDecimal()));
    }

    [Fact]
    public async Task TheResponse_CarriesNoBuyerDetails()
    {
        var show = Guid.CreateVersion7();
        Owns(show, "owner");
        await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 1, 120m));

        var raw = await (await GetAsync(show, "owner", "Organizer")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("customer", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.invalid", raw);
    }

    [Fact]
    public async Task AShowWithNoSales_Returns200WithZeros()
    {
        var show = Guid.CreateVersion7();
        Owns(show, "owner");

        var response = await GetAsync(show, "owner", "Organizer");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal((0, 0m, 0), (body.GetProperty("ticketsSold").GetInt32(), body.GetProperty("revenue").GetDecimal(), body.GetProperty("ticketsUsed").GetInt32()));
        Assert.Equal(0, body.GetProperty("categories").GetArrayLength());
    }

    [Fact]
    public async Task CancelledAndRefundedOrders_AreLeftOut()
    {
        var show = Guid.CreateVersion7();
        Owns(show, "owner");
        await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 1, 120m));
        await _data.AddOrderAsync(show, OrderStatus.Cancelled, "LKR", (Vip, 4, 120m));

        var body = await Json(await GetAsync(show, "owner", "Organizer"));

        Assert.Equal(1, body.GetProperty("ticketsSold").GetInt32());
        Assert.Equal(120m, body.GetProperty("revenue").GetDecimal());
    }

    [Fact]
    public async Task ANewSale_AppearsOnTheNextRequest_WithoutAnyCaching()
    {
        var show = Guid.CreateVersion7();
        Owns(show, "owner");
        Assert.Equal(0, (await Json(await GetAsync(show, "owner", "Organizer"))).GetProperty("ticketsSold").GetInt32());

        await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (General, 2, 40m));

        var body = await Json(await GetAsync(show, "owner", "Organizer"));
        Assert.Equal(2, body.GetProperty("ticketsSold").GetInt32());
        Assert.Equal(80m, body.GetProperty("revenue").GetDecimal());
    }

    [Fact]
    public async Task Ownership_IsCheckedOnEveryRequest_NotRemembered()
    {
        var show = Guid.CreateVersion7();
        Owns(show, "owner");
        await GetAsync(show, "owner", "Organizer");

        await GetAsync(show, "owner", "Organizer");

        Assert.Equal(2, _factory.EntryAccess.Calls);
    }

    [Fact]
    public async Task WhenCatalogCannotAnswer_TheRequestIsRefusedWith503AndNoFigures()
    {
        var show = Guid.CreateVersion7();
        await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 1, 120m));
        _factory.EntryAccess.Set(show, "owner", EntryAccessDecision.Unavailable);

        var response = await GetAsync(show, "owner", "Organizer");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.Contains("Retry-After"));
        Assert.DoesNotContain("ticketsSold", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ASuspendedOwner_StillReadsTheirOwnFigures()
    {
        // Catalog answers "allowed" for a suspended owner: suspension never hides sales already made.
        var show = Guid.CreateVersion7();
        Owns(show, "suspended-owner");
        await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 1, 120m));

        var response = await GetAsync(show, "suspended-owner", "Organizer");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OrdersInTwoCurrencies_ReturnAClearConflictInsteadOfAMixedTotal()
    {
        var show = Guid.CreateVersion7();
        Owns(show, "owner");
        await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 1, 120m));
        await _data.AddOrderAsync(show, OrderStatus.Confirmed, "USD", (Vip, 1, 5m));

        var response = await GetAsync(show, "owner", "Organizer");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("MixedCurrencies", (await Json(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ValidatingATicket_RaisesEntryProgressOnTheNextRead()
    {
        var show = Guid.CreateVersion7();
        Owns(show, "owner");
        var order = await _data.AddOrderAsync(show, OrderStatus.Confirmed, "LKR", (Vip, 2, 120m));
        var tickets = await _data.IssueTicketsAsync(order);
        Assert.Equal(0, (await Json(await GetAsync(show, "owner", "Organizer"))).GetProperty("ticketsUsed").GetInt32());

        var scan = new HttpRequestMessage(HttpMethod.Post, $"/api/booking/tickets/{tickets[0].UniqueCode}/validate");
        scan.Headers.Add("Test-Sub", "owner");
        scan.Headers.Add("Test-Role", "Organizer");
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().SendAsync(scan)).StatusCode);

        var body = await Json(await GetAsync(show, "owner", "Organizer"));
        Assert.Equal(1, body.GetProperty("ticketsUsed").GetInt32());
        Assert.Equal(2, body.GetProperty("ticketsSold").GetInt32());
    }
}
