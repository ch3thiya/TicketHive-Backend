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

// Real PostgreSQL through the real hold pipeline: seats are held exactly once, selections are
// all-or-nothing, and the seat follows the hold through expiry, payment and cancellation.
[Collection("HoldsApi")]
public sealed class SeatHoldTests
{
    private readonly HoldsApiFixture _fixture;

    public SeatHoldTests(HoldsApiFixture fixture) => _fixture = fixture;

    private DbConnectionFactory Connections() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = _fixture.ConnectionString })
        .Build());

    private HoldRepository Holds() => new(Connections(), new GeneralAdmissionAllocationStrategy(), new SeatedAllocationStrategy());

    private sealed record Seated(Guid Show, Guid Vip, Guid General, List<Guid> SeatIds);

    // 6 VIP seats (Front, rows A-B x 3) and 12 General seats (Hall, rows A-C x 4); 18 in all.
    private async Task<Seated> SeedSeatedAsync(int maxPerCustomer = 6)
    {
        var show = Guid.CreateVersion7();
        var vip = Guid.CreateVersion7();
        var general = Guid.CreateVersion7();
        await ExecuteAsync("INSERT INTO show_rules (show_id, organizer_id, max_per_customer, hold_minutes, high_demand) VALUES (@s, @o, @m, 10, false)",
            ("s", show), ("o", Guid.NewGuid()), ("m", maxPerCustomer));
        foreach (var (category, capacity) in new[] { (vip, 6), (general, 12) })
        {
            await ExecuteAsync("INSERT INTO stock (show_id, category_id, capacity, available, unit_price, currency) VALUES (@s, @c, @cap, @cap, 50, 'LKR')",
                ("s", show), ("c", category), ("cap", capacity));
        }

        var seats = Enumerable.Range(0, 2).SelectMany(r => Enumerable.Range(1, 3).Select(n => new SeatToInitialize(vip, "Front", ((char)('A' + r)).ToString(), n)))
            .Concat(Enumerable.Range(0, 3).SelectMany(r => Enumerable.Range(1, 4).Select(n => new SeatToInitialize(general, "Hall", ((char)('A' + r)).ToString(), n))))
            .ToList();
        var enabled = await new SeatLayoutRepository(Connections()).EnableAsync(show, 1, seats, DateTimeOffset.UtcNow);
        Assert.Equal(EnableSeatsOutcome.Enabled, enabled.Outcome);

        var ids = new List<Guid>();
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id FROM show_seats WHERE show_id = @s ORDER BY ordinal", connection);
        command.Parameters.AddWithValue("s", show);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
        return new Seated(show, vip, general, ids);
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }

    private Task<int> SeatsInAsync(Guid show, string status) =>
        ScalarAsync<int>("SELECT count(*)::int FROM show_seats WHERE show_id = @s AND status = @st", ("s", show), ("st", status));

    private Task<int> AvailableAsync(Guid show, Guid category) =>
        ScalarAsync<int>("SELECT available FROM stock WHERE show_id = @s AND category_id = @c", ("s", show), ("c", category));

    private Task<HttpResponseMessage> HoldSeatsAsync(Guid show, IEnumerable<Guid> seatIds, string customer, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/holds")
        {
            Content = JsonContent.Create(new { showId = show, seatIds })
        };
        request.Headers.Add(CustomerTestAuthHandler.SubHeaderName, customer);
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return _fixture.Factory.CreateClient().SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    // ---- AC2: exact seats ----

    [Fact]
    public async Task HoldingSeats_ReservesExactlyThoseSeatsAndReturnsTheirDetails()
    {
        var s = await SeedSeatedAsync();
        var chosen = new[] { s.SeatIds[0], s.SeatIds[1], s.SeatIds[6] };

        var response = await HoldSeatsAsync(s.Show, chosen, "buyer");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        var seats = body.GetProperty("seats").EnumerateArray().ToList();
        Assert.Equal(3, seats.Count);
        Assert.Contains(seats, x => x.GetProperty("section").GetString() == "Front" && x.GetProperty("row").GetString() == "A" && x.GetProperty("number").GetInt32() == 1);
        Assert.Equal(3, await SeatsInAsync(s.Show, "Held"));
        Assert.Equal(15, await SeatsInAsync(s.Show, "Available"));
        Assert.Equal(4, await AvailableAsync(s.Show, s.Vip));
        Assert.Equal(11, await AvailableAsync(s.Show, s.General));
        var items = body.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("categoryId").GetGuid(), i => i.GetProperty("quantity").GetInt32());
        Assert.Equal(2, items[s.Vip]);
        Assert.Equal(1, items[s.General]);
    }

    // ---- AC3: a seat is never held twice ----

    [Fact]
    public async Task ThirtyCustomersRacingForOneSeat_ExactlyOneWins_AndTheRestAreToldWhichSeat()
    {
        var s = await SeedSeatedAsync();
        var seat = s.SeatIds[4];

        var responses = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => HoldSeatsAsync(s.Show, new[] { seat }, $"racer-{i}")));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(29, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var loser = await Json(responses.First(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal("SeatsUnavailable", loser.GetProperty("code").GetString());
        Assert.Equal(seat, loser.GetProperty("unavailableSeats")[0].GetProperty("seatId").GetGuid());
        Assert.Equal(1, await SeatsInAsync(s.Show, "Held"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(DISTINCT hold_id)::int FROM show_seats WHERE id = @i", ("i", seat)));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*)::int FROM holds WHERE show_id = @s", ("s", s.Show)));
        Assert.Equal(5, await AvailableAsync(s.Show, s.Vip));
        Assert.Equal(12, await AvailableAsync(s.Show, s.General));
    }

    // ---- AC4: all or nothing ----

    [Fact]
    public async Task OneTakenSeatInASelection_HoldsNothingAndNamesTheSeat()
    {
        var s = await SeedSeatedAsync();
        Assert.Equal(HttpStatusCode.Created, (await HoldSeatsAsync(s.Show, new[] { s.SeatIds[7] }, "first")).StatusCode);
        var quotaBefore = await ScalarAsync<int>("SELECT COALESCE(SUM(quantity),0)::int FROM customer_quotas WHERE show_id = @s AND customer_sub = 'second'", ("s", s.Show));

        var response = await HoldSeatsAsync(s.Show, new[] { s.SeatIds[0], s.SeatIds[7], s.SeatIds[8] }, "second");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await Json(response);
        Assert.Contains("Hall A2", body.GetProperty("detail").GetString());
        Assert.Single(body.GetProperty("unavailableSeats").EnumerateArray());
        Assert.Equal(1, await SeatsInAsync(s.Show, "Held"));
        Assert.Equal(11, await AvailableAsync(s.Show, s.General));
        Assert.Equal(6, await AvailableAsync(s.Show, s.Vip));
        Assert.Equal(quotaBefore, await ScalarAsync<int>("SELECT COALESCE(SUM(quantity),0)::int FROM customer_quotas WHERE show_id = @s AND customer_sub = 'second'", ("s", s.Show)));
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*)::int FROM holds WHERE show_id = @s AND customer_sub = 'second'", ("s", s.Show)));
    }

    [Fact]
    public async Task UnknownSeatIds_AreRefusedAsUnavailable()
    {
        var s = await SeedSeatedAsync();

        var response = await HoldSeatsAsync(s.Show, new[] { s.SeatIds[0], Guid.NewGuid() }, "buyer");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await SeatsInAsync(s.Show, "Held"));
    }

    [Fact]
    public async Task SeatsOfAnotherShow_CannotBeHeldThroughThisOne()
    {
        var a = await SeedSeatedAsync();
        var b = await SeedSeatedAsync();

        var response = await HoldSeatsAsync(a.Show, new[] { b.SeatIds[0] }, "buyer");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await SeatsInAsync(b.Show, "Held"));
    }

    [Fact]
    public async Task OverlappingMultiSeatSelections_NeverDoubleHoldNeverDeadlockAndKeepCountersExact()
    {
        var s = await SeedSeatedAsync();
        var random = new Random(167);
        var picks = Enumerable.Range(0, 40)
            .Select(i => (Customer: $"overlap-{i}", Seats: s.SeatIds.OrderBy(_ => random.Next()).Take(random.Next(2, 5)).ToList()))
            .ToList();

        var responses = await Task.WhenAll(picks.Select(p => HoldSeatsAsync(s.Show, p.Seats, p.Customer)));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity, $"unexpected {(int)r.StatusCode}"));
        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.True(created > 0);

        // Every held seat belongs to exactly one live hold, and each hold has all of its seats.
        var held = await SeatsInAsync(s.Show, "Held");
        Assert.Equal(await ScalarAsync<int>("SELECT COALESCE(SUM(hi.quantity),0)::int FROM hold_items hi JOIN holds h ON h.id = hi.hold_id WHERE h.show_id = @s", ("s", s.Show)), held);
        Assert.Equal(18 - held, await AvailableAsync(s.Show, s.Vip) + await AvailableAsync(s.Show, s.General));
        Assert.Equal(created, await ScalarAsync<int>("SELECT count(*)::int FROM holds WHERE show_id = @s", ("s", s.Show)));
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*)::int FROM show_seats WHERE show_id = @s AND (status = 'Available') <> (hold_id IS NULL)", ("s", s.Show)));
    }

    // ---- quota and mode rules ----

    [Fact]
    public async Task TheSeatCountCountsTowardThePerCustomerLimit()
    {
        var s = await SeedSeatedAsync(maxPerCustomer: 6);

        var tooMany = await HoldSeatsAsync(s.Show, s.SeatIds.Take(7), "greedy");
        var four = await HoldSeatsAsync(s.Show, s.SeatIds.Skip(8).Take(4), "greedy");
        var three = await HoldSeatsAsync(s.Show, s.SeatIds.Skip(12).Take(3), "greedy");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.Created, four.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, three.StatusCode);
        Assert.Equal(4, await SeatsInAsync(s.Show, "Held"));
    }

    [Fact]
    public async Task RepeatingTheSameRequestReplaysTheOriginalHold()
    {
        var s = await SeedSeatedAsync();
        var key = Guid.NewGuid().ToString();

        var first = await HoldSeatsAsync(s.Show, s.SeatIds.Take(2), "retrier", key);
        var second = await HoldSeatsAsync(s.Show, s.SeatIds.Take(2), "retrier", key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal((await Json(first)).GetProperty("holdId").GetGuid(), (await Json(second)).GetProperty("holdId").GetGuid());
        Assert.Equal(2, await SeatsInAsync(s.Show, "Held"));
        Assert.Equal(2, await ScalarAsync<int>("SELECT quantity FROM customer_quotas WHERE show_id = @s AND customer_sub = 'retrier'", ("s", s.Show)));
    }

    [Fact]
    public async Task ParallelCopiesOfTheSameRequest_ProduceOneHold()
    {
        var s = await SeedSeatedAsync();
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => HoldSeatsAsync(s.Show, s.SeatIds.Take(2), "retrier", key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*)::int FROM holds WHERE show_id = @s", ("s", s.Show)));
        Assert.Equal(2, await SeatsInAsync(s.Show, "Held"));
    }

    [Fact]
    public async Task SeatedShow_RefusesQuantityHolds_AndGaShowRefusesSeats()
    {
        var s = await SeedSeatedAsync();
        var gaShow = Guid.NewGuid();
        var gaCategory = Guid.NewGuid();
        await ExecuteAsync("INSERT INTO show_rules (show_id, organizer_id, max_per_customer, hold_minutes, high_demand) VALUES (@s, @o, 6, 10, false)", ("s", gaShow), ("o", Guid.NewGuid()));
        await ExecuteAsync("INSERT INTO stock (show_id, category_id, capacity, available, unit_price, currency) VALUES (@s, @c, 10, 10, 50, 'LKR')", ("s", gaShow), ("c", gaCategory));

        var quantity = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/holds") { Content = JsonContent.Create(new { showId = s.Show, items = new[] { new { categoryId = s.General, quantity = 1 } } }) };
        quantity.Headers.Add(CustomerTestAuthHandler.SubHeaderName, "buyer");
        quantity.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var seatedWithQuantity = await _fixture.Factory.CreateClient().SendAsync(quantity);
        var gaWithSeats = await HoldSeatsAsync(gaShow, new[] { s.SeatIds[0] }, "buyer");
        var gaNormal = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/holds") { Content = JsonContent.Create(new { showId = gaShow, items = new[] { new { categoryId = gaCategory, quantity = 2 } } }) };
        gaNormal.Headers.Add(CustomerTestAuthHandler.SubHeaderName, "buyer");
        gaNormal.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var ga = await _fixture.Factory.CreateClient().SendAsync(gaNormal);

        Assert.Equal(HttpStatusCode.Conflict, seatedWithQuantity.StatusCode);
        Assert.Equal("SeatsRequired", (await Json(seatedWithQuantity)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, gaWithSeats.StatusCode);
        Assert.Equal("SeatsNotSupported", (await Json(gaWithSeats)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, ga.StatusCode);
        Assert.Equal(8, await AvailableAsync(gaShow, gaCategory));
        Assert.Equal(0, await SeatsInAsync(s.Show, "Held"));
    }

    [Fact]
    public async Task RequestingBothItemsAndSeats_Or_DuplicateSeats_Returns400()
    {
        var s = await SeedSeatedAsync();
        var both = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/holds") { Content = JsonContent.Create(new { showId = s.Show, items = new[] { new { categoryId = s.General, quantity = 1 } }, seatIds = new[] { s.SeatIds[0] } }) };
        both.Headers.Add(CustomerTestAuthHandler.SubHeaderName, "buyer");
        both.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.BadRequest, (await _fixture.Factory.CreateClient().SendAsync(both)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await HoldSeatsAsync(s.Show, new[] { s.SeatIds[0], s.SeatIds[0] }, "buyer")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await HoldSeatsAsync(s.Show, Array.Empty<Guid>(), "buyer")).StatusCode);
        Assert.Equal(0, await SeatsInAsync(s.Show, "Held"));
    }

    // ---- AC6: the seat follows the hold ----

    private async Task<Guid> HoldAsync(Seated s, int count, string customer, int skip = 0)
    {
        var response = await HoldSeatsAsync(s.Show, s.SeatIds.Skip(skip).Take(count), customer);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("holdId").GetGuid();
    }

    [Fact]
    public async Task ExpiredHold_ReturnsItsSeatsAndStock()
    {
        var s = await SeedSeatedAsync();
        var hold = await HoldAsync(s, 3, "slow");
        await ExecuteAsync("UPDATE holds SET expires_at = now() - interval '1 minute' WHERE id = @h", ("h", hold));

        var summary = await Holds().ReleaseExpiredHoldsAsync(DateTimeOffset.UtcNow);

        Assert.True(summary.HoldsReleased >= 1);
        Assert.Equal(0, await SeatsInAsync(s.Show, "Held"));
        Assert.Equal(6, await AvailableAsync(s.Show, s.Vip));
        Assert.Equal(12, await AvailableAsync(s.Show, s.General));
        Assert.Equal(HttpStatusCode.Created, (await HoldSeatsAsync(s.Show, s.SeatIds.Take(3), "next")).StatusCode);
    }

    [Fact]
    public async Task CancelledHold_ReturnsItsSeats()
    {
        var s = await SeedSeatedAsync();
        var hold = await HoldAsync(s, 2, "changed-mind");

        Assert.True(await Holds().CancelHoldAsync(hold, "changed-mind", DateTimeOffset.UtcNow));

        Assert.Equal(0, await SeatsInAsync(s.Show, "Held"));
        Assert.Equal(6, await AvailableAsync(s.Show, s.Vip));
    }

    [Fact]
    public async Task PaidHold_MakesItsSeatsSold_AndKeepsThemAfterTheOriginalExpiry()
    {
        var s = await SeedSeatedAsync();
        var hold = await HoldAsync(s, 3, "payer");
        var repository = Holds();

        Assert.True(await repository.FreezeHoldAsync(hold, DateTimeOffset.UtcNow));
        Assert.Equal(3, await SeatsInAsync(s.Show, "Held"));
        Assert.True(await repository.ConvertHoldAsync(hold));

        Assert.Equal(3, await SeatsInAsync(s.Show, "Sold"));
        Assert.Equal(0, await SeatsInAsync(s.Show, "Held"));
        await ExecuteAsync("UPDATE holds SET expires_at = now() - interval '1 hour' WHERE id = @h", ("h", hold));
        await repository.ReleaseExpiredHoldsAsync(DateTimeOffset.UtcNow);
        Assert.Equal(3, await SeatsInAsync(s.Show, "Sold"));
        Assert.Equal(HttpStatusCode.Conflict, (await HoldSeatsAsync(s.Show, s.SeatIds.Take(1), "late")).StatusCode);
    }

    [Fact]
    public async Task FailedPayment_ReleasesTheFrozenHoldsSeats()
    {
        var s = await SeedSeatedAsync();
        var hold = await HoldAsync(s, 2, "declined");
        var repository = Holds();
        await repository.FreezeHoldAsync(hold, DateTimeOffset.UtcNow);

        Assert.True(await repository.ReleaseHoldAsync(hold));

        Assert.Equal(0, await SeatsInAsync(s.Show, "Held"));
        Assert.Equal(6, await AvailableAsync(s.Show, s.Vip));
    }

    [Fact]
    public async Task OrderCancellation_ReturnsSoldSeatsOnce_AndTheyCanBeBoughtAgain()
    {
        var s = await SeedSeatedAsync();
        var hold = await HoldAsync(s, 3, "refunded");
        var repository = Holds();
        await repository.FreezeHoldAsync(hold, DateTimeOffset.UtcNow);
        await repository.ConvertHoldAsync(hold);
        Assert.Equal(3, await SeatsInAsync(s.Show, "Sold"));

        // The same function Inventory's cancellation endpoint runs; repeating it must change nothing.
        for (var i = 0; i < 3; i++)
        {
            Assert.True(await ScalarAsync<bool>("SELECT return_cancelled_hold(@h)", ("h", hold)));
        }

        Assert.Equal(0, await SeatsInAsync(s.Show, "Sold"));
        Assert.Equal(18, await SeatsInAsync(s.Show, "Available"));
        Assert.Equal(6, await AvailableAsync(s.Show, s.Vip));
        Assert.Equal(12, await AvailableAsync(s.Show, s.General));
        Assert.Equal(0, await ScalarAsync<int>("SELECT sold::int FROM stock WHERE show_id = @s AND category_id = @c", ("s", s.Show), ("c", s.Vip)));
        Assert.Equal(HttpStatusCode.Created, (await HoldSeatsAsync(s.Show, s.SeatIds.Take(3), "someone-else")).StatusCode);
    }

    [Fact]
    public async Task ShowCancellation_ClosesActiveSeatedHolds_AndReturnsTheirSeats()
    {
        var s = await SeedSeatedAsync();
        await HoldAsync(s, 2, "open-1");
        await HoldAsync(s, 2, "open-2", skip: 6);

        await ExecuteAsync("SELECT close_cancelled_show(@s)", ("s", s.Show));

        Assert.Equal(0, await SeatsInAsync(s.Show, "Held"));
        Assert.Equal(18, await SeatsInAsync(s.Show, "Available"));
    }

    [Fact]
    public async Task ExpiryAndNewHoldsAndEnablesRacing_NeverDeadlockOrLoseTrack()
    {
        var s = await SeedSeatedAsync();
        var holds = new List<Guid>();
        for (var i = 0; i < 6; i++) holds.Add(await HoldAsync(s, 1, $"expiring-{i}", skip: i));
        await ExecuteAsync("UPDATE holds SET expires_at = now() - interval '1 minute' WHERE show_id = @s", ("s", s.Show));

        var sweeps = Enumerable.Range(0, 3).Select(_ => Holds().ReleaseExpiredHoldsAsync(DateTimeOffset.UtcNow));
        var fresh = Enumerable.Range(0, 12).Select(i => HoldSeatsAsync(s.Show, s.SeatIds.Skip(i % 6).Take(1), $"fresh-{i}"));
        await Task.WhenAll(sweeps);
        var responses = await Task.WhenAll(fresh);

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, $"unexpected {(int)r.StatusCode}"));
        var held = await SeatsInAsync(s.Show, "Held");
        Assert.Equal(await ScalarAsync<int>("SELECT COALESCE(SUM(hi.quantity),0)::int FROM hold_items hi JOIN holds h ON h.id = hi.hold_id WHERE h.show_id = @s AND h.status IN ('Active','PaymentPending')", ("s", s.Show)), held);
        Assert.Equal(18 - held, await AvailableAsync(s.Show, s.Vip) + await AvailableAsync(s.Show, s.General));
    }
}
