using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Service.Db;
using Inventory.Service.Models;
using Inventory.Service.Tests.Api;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Inventory.Service.Tests.Integration;

// The public seat map: anonymous, live, never cached, and no customer data.
[Collection("HoldsApi")]
public sealed class SeatMapApiTests
{
    private readonly HoldsApiFixture _fixture;

    public SeatMapApiTests(HoldsApiFixture fixture) => _fixture = fixture;

    private DbConnectionFactory Connections() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = _fixture.ConnectionString })
        .Build());

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    // Two sections: Front (VIP, 2 rows x 2) and Hall (General, 1 row x 3).
    private async Task<(Guid Show, Guid Vip, Guid General)> SeedAsync()
    {
        var show = Guid.CreateVersion7();
        var vip = Guid.CreateVersion7();
        var general = Guid.CreateVersion7();
        await ExecuteAsync("INSERT INTO show_rules (show_id, organizer_id, max_per_customer, hold_minutes, high_demand) VALUES (@s, @o, 6, 10, false)", ("s", show), ("o", Guid.NewGuid()));
        await ExecuteAsync("INSERT INTO stock (show_id, category_id, capacity, available, unit_price, currency) VALUES (@s, @c, 4, 4, 120.50, 'LKR')", ("s", show), ("c", vip));
        await ExecuteAsync("INSERT INTO stock (show_id, category_id, capacity, available, unit_price, currency) VALUES (@s, @c, 3, 3, 40, 'LKR')", ("s", show), ("c", general));
        var seats = new List<SeatToInitialize>
        {
            new(vip, "Front", "A", 1), new(vip, "Front", "A", 2), new(vip, "Front", "B", 1), new(vip, "Front", "B", 2),
            new(general, "Hall", "A", 1), new(general, "Hall", "A", 2), new(general, "Hall", "A", 3)
        };
        await new SeatLayoutRepository(Connections()).EnableAsync(show, 1, seats, DateTimeOffset.UtcNow);
        return (show, vip, general);
    }

    private HttpClient Client() => _fixture.Factory.CreateClient();

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<HttpResponseMessage> HoldAsync(Guid show, Guid seat, string customer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/holds") { Content = JsonContent.Create(new { showId = show, seatIds = new[] { seat } }) };
        request.Headers.Add(CustomerTestAuthHandler.SubHeaderName, customer);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await Client().SendAsync(request);
    }

    [Fact]
    public async Task SeatMap_IsAnonymousAndNeverCached()
    {
        var s = await SeedAsync();

        var response = await Client().GetAsync($"/api/inventory/shows/{s.Show}/seats");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        var states = await Client().GetAsync($"/api/inventory/shows/{s.Show}/seats/states");
        Assert.Equal(HttpStatusCode.OK, states.StatusCode);
        Assert.Contains("no-store", states.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task SeatMap_ListsSectionsRowsSeatsCategoriesAndPrices_InLayoutOrder()
    {
        var s = await SeedAsync();

        var map = await Json(await Client().GetAsync($"/api/inventory/shows/{s.Show}/seats"));

        Assert.Equal(1, map.GetProperty("version").GetInt32());
        var sections = map.GetProperty("sections").EnumerateArray().ToList();
        Assert.Equal(new[] { "Front", "Hall" }, sections.Select(x => x.GetProperty("name").GetString()));
        var front = sections[0].GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(new[] { "A", "B" }, front.Select(r => r.GetProperty("label").GetString()));
        Assert.Equal(new[] { 1, 2 }, front[0].GetProperty("seats").EnumerateArray().Select(x => x.GetProperty("number").GetInt32()));
        Assert.All(sections.SelectMany(x => x.GetProperty("rows").EnumerateArray()).SelectMany(r => r.GetProperty("seats").EnumerateArray()),
            seat => Assert.Equal("Available", seat.GetProperty("state").GetString()));
        var categories = map.GetProperty("categories").EnumerateArray().ToDictionary(c => c.GetProperty("categoryId").GetGuid());
        Assert.Equal(120.50m, categories[s.Vip].GetProperty("unitPrice").GetDecimal());
        Assert.Equal("LKR", categories[s.General].GetProperty("currency").GetString());
        Assert.Equal(s.Vip, front[0].GetProperty("seats")[0].GetProperty("categoryId").GetGuid());
    }

    [Fact]
    public async Task SeatStates_FollowHoldsAndSales_WithoutAnyCustomerData()
    {
        var s = await SeedAsync();
        var map = await Json(await Client().GetAsync($"/api/inventory/shows/{s.Show}/seats"));
        var seatIds = map.GetProperty("sections").EnumerateArray().SelectMany(x => x.GetProperty("rows").EnumerateArray())
            .SelectMany(r => r.GetProperty("seats").EnumerateArray()).Select(x => x.GetProperty("id").GetGuid()).ToList();

        var empty = await Json(await Client().GetAsync($"/api/inventory/shows/{s.Show}/seats/states"));
        Assert.Empty(empty.GetProperty("unavailable").EnumerateArray());

        var holdResponse = await HoldAsync(s.Show, seatIds[0], "someone@example.com");
        Assert.Equal(HttpStatusCode.Created, holdResponse.StatusCode);
        var held = await Json(await Client().GetAsync($"/api/inventory/shows/{s.Show}/seats/states"));
        var entry = Assert.Single(held.GetProperty("unavailable").EnumerateArray());
        Assert.Equal(seatIds[0], entry.GetProperty("id").GetGuid());
        Assert.Equal("Held", entry.GetProperty("state").GetString());
        var raw = await (await Client().GetAsync($"/api/inventory/shows/{s.Show}/seats")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("someone@example.com", raw);
        Assert.DoesNotContain("customer", raw, StringComparison.OrdinalIgnoreCase);

        var holdId = (await Json(holdResponse)).GetProperty("holdId").GetGuid();
        var repository = new HoldRepository(Connections(), new GeneralAdmissionAllocationStrategy(), new SeatedAllocationStrategy());
        await repository.FreezeHoldAsync(holdId, DateTimeOffset.UtcNow);
        await repository.ConvertHoldAsync(holdId);
        var sold = await Json(await Client().GetAsync($"/api/inventory/shows/{s.Show}/seats/states"));
        Assert.Equal("Sold", Assert.Single(sold.GetProperty("unavailable").EnumerateArray()).GetProperty("state").GetString());

        await ExecuteAsync("SELECT return_cancelled_hold(@h)", ("h", holdId));
        var back = await Json(await Client().GetAsync($"/api/inventory/shows/{s.Show}/seats/states"));
        Assert.Empty(back.GetProperty("unavailable").EnumerateArray());
    }

    [Fact]
    public async Task ShowsWithoutASeatMap_Return404()
    {
        var general = Guid.CreateVersion7();
        await ExecuteAsync("INSERT INTO show_rules (show_id, organizer_id, max_per_customer, hold_minutes, high_demand) VALUES (@s, @o, 6, 10, false)", ("s", general), ("o", Guid.NewGuid()));
        await ExecuteAsync("INSERT INTO stock (show_id, category_id, capacity, available, unit_price, currency) VALUES (@s, @c, 10, 10, 50, 'LKR')", ("s", general), ("c", Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync($"/api/inventory/shows/{general}/seats")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync($"/api/inventory/shows/{general}/seats/states")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync($"/api/inventory/shows/{Guid.NewGuid()}/seats")).StatusCode);
    }
}
