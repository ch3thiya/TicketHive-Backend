using System.Net;
using System.Net.Http.Json;
using Inventory.Service.Db;
using Inventory.Service.Models;
using Inventory.Service.Tests.Api;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Inventory.Service.Tests.Integration;

// Real PostgreSQL. Enabling a seat layout races real holds through the real HTTP pipeline: the stock
// row lock must make exactly one of them win, and seats must never be duplicated.
[Collection("HoldsApi")]
public sealed class SeatLayoutRepositoryTests
{
    private readonly HoldsApiFixture _fixture;

    public SeatLayoutRepositoryTests(HoldsApiFixture fixture) => _fixture = fixture;

    private SeatLayoutRepository Repository() => new(new DbConnectionFactory(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = _fixture.ConnectionString })
        .Build()));

    private static List<SeatToInitialize> Seats(Guid category, string section, int rows, int perRow) =>
        Enumerable.Range(0, rows).SelectMany(r => Enumerable.Range(1, perRow).Select(n => new SeatToInitialize(category, section, ((char)('A' + r)).ToString(), n))).ToList();

    private async Task<(Guid Show, Guid Vip, Guid General)> SeedAsync(int vip = 6, int general = 12)
    {
        var show = Guid.CreateVersion7();
        var vipId = Guid.CreateVersion7();
        var generalId = Guid.CreateVersion7();
        await ExecuteAsync("INSERT INTO show_rules (show_id, organizer_id, max_per_customer, hold_minutes, high_demand) VALUES (@s, @o, 6, 10, false)",
            ("s", show), ("o", Guid.NewGuid()));
        foreach (var (category, capacity) in new[] { (vipId, vip), (generalId, general) })
        {
            await ExecuteAsync("INSERT INTO stock (show_id, category_id, capacity, available, unit_price, currency) VALUES (@s, @c, @cap, @cap, 50, 'LKR')",
                ("s", show), ("c", category), ("cap", capacity));
        }

        return (show, vipId, generalId);
    }

    private static List<SeatToInitialize> FullLayout((Guid Show, Guid Vip, Guid General) s, int vip = 6, int general = 12) =>
        Seats(s.Vip, "Front", 2, vip / 2).Concat(Seats(s.General, "Hall", 3, general / 3)).ToList();

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

    private Task<int> SeatCountAsync(Guid show) => ScalarAsync<int>("SELECT count(*)::int FROM show_seats WHERE show_id = @s", ("s", show));
    private Task<string> ModeAsync(Guid show, Guid category) => ScalarAsync<string>("SELECT allocation_mode FROM stock WHERE show_id = @s AND category_id = @c", ("s", show), ("c", category));

    private Task<HttpResponseMessage> HoldAsync(Guid show, Guid category, string customer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/holds")
        {
            Content = JsonContent.Create(new { showId = show, items = new[] { new { categoryId = category, quantity = 1 } } })
        };
        request.Headers.Add(CustomerTestAuthHandler.SubHeaderName, customer);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return _fixture.Factory.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task Enable_CreatesEverySeatOnceAndSwitchesStockToSeated()
    {
        var s = await SeedAsync();

        var result = await Repository().EnableAsync(s.Show, 1, FullLayout(s), DateTimeOffset.UtcNow);

        Assert.Equal(EnableSeatsOutcome.Enabled, result.Outcome);
        Assert.Equal(18, await SeatCountAsync(s.Show));
        Assert.Equal(6, await ScalarAsync<int>("SELECT count(*)::int FROM show_seats WHERE show_id = @s AND category_id = @c AND status = 'Available'", ("s", s.Show), ("c", s.Vip)));
        Assert.Equal("Seated", await ModeAsync(s.Show, s.Vip));
        Assert.Equal("Seated", await ModeAsync(s.Show, s.General));
        Assert.Equal(12, await ScalarAsync<int>("SELECT available FROM stock WHERE show_id = @s AND category_id = @c", ("s", s.Show), ("c", s.General)));
    }

    [Fact]
    public async Task Enable_RepeatedForTheSameVersion_ChangesNothing()
    {
        var s = await SeedAsync();
        var repository = Repository();
        await repository.EnableAsync(s.Show, 1, FullLayout(s), DateTimeOffset.UtcNow);

        var again = await repository.EnableAsync(s.Show, 1, FullLayout(s), DateTimeOffset.UtcNow);

        Assert.Equal(EnableSeatsOutcome.AlreadyEnabled, again.Outcome);
        Assert.Equal(18, await SeatCountAsync(s.Show));
    }

    [Fact]
    public async Task Enable_ManyParallelRetriesForOneVersion_CreateSeatsExactlyOnce()
    {
        var s = await SeedAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Repository().EnableAsync(s.Show, 1, FullLayout(s), DateTimeOffset.UtcNow)));

        Assert.Equal(1, results.Count(r => r.Outcome == EnableSeatsOutcome.Enabled));
        Assert.Equal(19, results.Count(r => r.Outcome == EnableSeatsOutcome.AlreadyEnabled));
        Assert.Equal(18, await SeatCountAsync(s.Show));
    }

    [Fact]
    public async Task Enable_CategoryCountDifferentFromCapacity_IsRefusedAndChangesNothing()
    {
        var s = await SeedAsync();
        var seats = Seats(s.Vip, "Front", 2, 2).Concat(Seats(s.General, "Hall", 3, 4)).ToList();

        var result = await Repository().EnableAsync(s.Show, 1, seats, DateTimeOffset.UtcNow);

        Assert.Equal(EnableSeatsOutcome.Mismatch, result.Outcome);
        Assert.Equal(0, await SeatCountAsync(s.Show));
        Assert.Equal("GA", await ModeAsync(s.Show, s.Vip));
    }

    [Fact]
    public async Task Enable_CategoryMissingFromTheLayout_IsRefused()
    {
        var s = await SeedAsync();

        var result = await Repository().EnableAsync(s.Show, 1, Seats(s.Vip, "Front", 2, 3), DateTimeOffset.UtcNow);

        Assert.Equal(EnableSeatsOutcome.Mismatch, result.Outcome);
    }

    [Fact]
    public async Task Enable_UnknownShow_ReportsMissingStock()
    {
        var result = await Repository().EnableAsync(Guid.NewGuid(), 1, Seats(Guid.NewGuid(), "A", 1, 1), DateTimeOffset.UtcNow);

        Assert.Equal(EnableSeatsOutcome.StockMissing, result.Outcome);
    }

    [Fact]
    public async Task Enable_AfterAHoldExists_IsLockedAndLeavesStockAsGa()
    {
        var s = await SeedAsync();
        var hold = await HoldAsync(s.Show, s.General, "buyer");
        Assert.Equal(HttpStatusCode.Created, hold.StatusCode);

        var result = await Repository().EnableAsync(s.Show, 1, FullLayout(s), DateTimeOffset.UtcNow);

        Assert.Equal(EnableSeatsOutcome.Locked, result.Outcome);
        Assert.Equal(0, await SeatCountAsync(s.Show));
        Assert.Equal("GA", await ModeAsync(s.Show, s.General));
        Assert.True((await Repository().GetLockStateAsync(s.Show)).Locked);
    }

    [Fact]
    public async Task Enable_AfterTicketsAreSold_IsLocked()
    {
        var s = await SeedAsync();
        await ExecuteAsync("UPDATE stock SET sold = 1, available = available - 1 WHERE show_id = @s AND category_id = @c", ("s", s.Show), ("c", s.General));

        var result = await Repository().EnableAsync(s.Show, 1, FullLayout(s), DateTimeOffset.UtcNow);

        Assert.Equal(EnableSeatsOutcome.Locked, result.Outcome);
    }

    [Fact]
    public async Task Enable_AfterTheHoldWasCancelled_IsAllowed()
    {
        var s = await SeedAsync();
        await HoldAsync(s.Show, s.General, "buyer");
        await ExecuteAsync("UPDATE stock SET available = capacity WHERE show_id = @s", ("s", s.Show));
        await ExecuteAsync("UPDATE holds SET status = 'Cancelled' WHERE show_id = @s", ("s", s.Show));

        var result = await Repository().EnableAsync(s.Show, 1, FullLayout(s), DateTimeOffset.UtcNow);

        Assert.Equal(EnableSeatsOutcome.Enabled, result.Outcome);
    }

    [Fact]
    public async Task SeatedShow_RejectsGeneralAdmissionHolds_AndStockIsUntouched()
    {
        var s = await SeedAsync();
        await Repository().EnableAsync(s.Show, 1, FullLayout(s), DateTimeOffset.UtcNow);

        var hold = await HoldAsync(s.Show, s.General, "buyer");

        Assert.Equal(HttpStatusCode.Conflict, hold.StatusCode);
        Assert.Equal(12, await ScalarAsync<int>("SELECT available FROM stock WHERE show_id = @s AND category_id = @c", ("s", s.Show), ("c", s.General)));
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*)::int FROM holds WHERE show_id = @s", ("s", s.Show)));
    }

    [Fact]
    public async Task NewVersion_ReplacesUntouchedSeats_AndOlderVersionIsStale()
    {
        var s = await SeedAsync();
        var repository = Repository();
        await repository.EnableAsync(s.Show, 1, FullLayout(s), DateTimeOffset.UtcNow);
        var different = Seats(s.Vip, "Balcony", 1, 6).Concat(Seats(s.General, "Floor", 2, 6)).ToList();

        var v2 = await repository.EnableAsync(s.Show, 2, different, DateTimeOffset.UtcNow);
        var v1Again = await repository.EnableAsync(s.Show, 1, FullLayout(s), DateTimeOffset.UtcNow);

        Assert.Equal(EnableSeatsOutcome.Enabled, v2.Outcome);
        Assert.Equal(EnableSeatsOutcome.StaleVersion, v1Again.Outcome);
        Assert.Equal(18, await SeatCountAsync(s.Show));
        Assert.Equal(6, await ScalarAsync<int>("SELECT count(*)::int FROM show_seats WHERE show_id = @s AND section = 'Balcony'", ("s", s.Show)));
        Assert.Equal(2, (await repository.GetLockStateAsync(s.Show)).Version);
    }

    [Fact]
    public async Task NewVersion_AfterASeatIsNoLongerAvailable_IsLocked()
    {
        var s = await SeedAsync();
        var repository = Repository();
        await repository.EnableAsync(s.Show, 1, FullLayout(s), DateTimeOffset.UtcNow);
        await ExecuteAsync("UPDATE show_seats SET status = 'Held' WHERE id = (SELECT id FROM show_seats WHERE show_id = @s LIMIT 1)", ("s", s.Show));

        var v2 = await repository.EnableAsync(s.Show, 2, FullLayout(s), DateTimeOffset.UtcNow);

        Assert.Equal(EnableSeatsOutcome.Locked, v2.Outcome);
        Assert.Equal(1, (await repository.GetLockStateAsync(s.Show)).Version);
    }

    [Fact]
    public async Task EnableRacingAHold_ExactlyOneWins_AndStockStaysConsistent()
    {
        for (var round = 0; round < 25; round++)
        {
            var s = await SeedAsync();

            var enable = Repository().EnableAsync(s.Show, 1, FullLayout(s), DateTimeOffset.UtcNow);
            var hold = HoldAsync(s.Show, s.General, $"racer-{round}");
            await Task.WhenAll(enable, hold);
            var enableResult = await enable;
            var holdResponse = await hold;

            var holdStatus = holdResponse.StatusCode;
            var seats = await SeatCountAsync(s.Show);
            var mode = await ModeAsync(s.Show, s.General);
            var available = await ScalarAsync<int>("SELECT available FROM stock WHERE show_id = @s AND category_id = @c", ("s", s.Show), ("c", s.General));

            if (enableResult.Outcome == EnableSeatsOutcome.Enabled)
            {
                // The layout won: seats exist, stock is seated and the GA hold was refused.
                Assert.Equal(HttpStatusCode.Conflict, holdStatus);
                Assert.Equal(18, seats);
                Assert.Equal("Seated", mode);
                Assert.Equal(12, available);
            }
            else
            {
                // The hold won: the layout is locked, nothing seated, the hold owns its ticket.
                Assert.Equal(EnableSeatsOutcome.Locked, enableResult.Outcome);
                Assert.Equal(HttpStatusCode.Created, holdStatus);
                Assert.Equal(0, seats);
                Assert.Equal("GA", mode);
                Assert.Equal(11, available);
            }
        }
    }

    [Theory]
    [InlineData("hold")]
    [InlineData("sold")]
    public async Task NewVersion_OnASeatedShowWithAHoldOrASale_IsLockedAndTheOldSeatsStay(string cause)
    {
        var s = await SeedAsync();
        var repository = Repository();
        await repository.EnableAsync(s.Show, 1, FullLayout(s), DateTimeOffset.UtcNow);
        if (cause == "hold")
        {
            await ExecuteAsync("INSERT INTO holds (id, show_id, customer_sub, status, expires_at, idempotency_key, created_at) VALUES (@id, @s, 'c', 'Active', now() + interval '10 minutes', @k, now())",
                ("id", Guid.NewGuid()), ("s", s.Show), ("k", Guid.NewGuid().ToString()));
        }
        else
        {
            await ExecuteAsync("UPDATE stock SET sold = 1, available = available - 1 WHERE show_id = @s AND category_id = @c", ("s", s.Show), ("c", s.General));
        }

        var different = Seats(s.Vip, "Balcony", 1, 6).Concat(Seats(s.General, "Floor", 2, 6)).ToList();
        var v2 = await repository.EnableAsync(s.Show, 2, different, DateTimeOffset.UtcNow);

        Assert.Equal(EnableSeatsOutcome.Locked, v2.Outcome);
        Assert.Equal(18, await SeatCountAsync(s.Show));
        Assert.Equal(0, await ScalarAsync<int>("SELECT count(*)::int FROM show_seats WHERE show_id = @s AND section = 'Balcony'", ("s", s.Show)));
        Assert.Equal(1, (await repository.GetLockStateAsync(s.Show)).Version);
        Assert.True((await repository.GetLockStateAsync(s.Show)).Locked);
    }

    [Fact]
    public async Task NewVersionRacingAHold_OnASeatedShow_NeverLeavesMixedSeats()
    {
        for (var round = 0; round < 15; round++)
        {
            var s = await SeedAsync();
            var repository = Repository();
            await repository.EnableAsync(s.Show, 1, FullLayout(s), DateTimeOffset.UtcNow);
            var different = Seats(s.Vip, "Balcony", 1, 6).Concat(Seats(s.General, "Floor", 2, 6)).ToList();

            var hold = ExecuteAsync("INSERT INTO holds (id, show_id, customer_sub, status, expires_at, idempotency_key, created_at) VALUES (@id, @s, 'c', 'Active', now() + interval '10 minutes', @k, now())",
                ("id", Guid.NewGuid()), ("s", s.Show), ("k", Guid.NewGuid().ToString()));
            var enable = repository.EnableAsync(s.Show, 2, different, DateTimeOffset.UtcNow);
            await Task.WhenAll(hold, enable);

            // Whichever order the database picked, the show has exactly one complete layout.
            Assert.Equal(18, await SeatCountAsync(s.Show));
            var sections = await ScalarAsync<int>("SELECT count(DISTINCT section)::int FROM show_seats WHERE show_id = @s", ("s", s.Show));
            Assert.Equal(2, sections);
            var version = (await repository.GetLockStateAsync(s.Show)).Version;
            Assert.Equal((await enable).Outcome == EnableSeatsOutcome.Enabled ? 2 : 1, version);
        }
    }
}
