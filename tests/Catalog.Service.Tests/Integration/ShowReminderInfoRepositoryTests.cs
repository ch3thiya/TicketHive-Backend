using Catalog.Service.Db;
using Catalog.Service.Models;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Catalog.Service.Tests.Integration;

// Real PostgreSQL: the batch lookup Notification uses to decide whether and what to remind.
[Collection("Postgres")]
public sealed class ShowReminderInfoRepositoryTests
{
    private readonly PostgresFixture _db;

    public ShowReminderInfoRepositoryTests(PostgresFixture db) => _db = db;

    private DbConnectionFactory Connections() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString })
        .Build());

    private async Task<Guid> AddVenueAsync(string name)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("INSERT INTO venues (id, name, address, capacity, created_at, updated_at) VALUES (@i, @n, 'Colombo', 100, now(), now())", connection);
        command.Parameters.AddWithValue("i", id);
        command.Parameters.AddWithValue("n", name);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<(Guid ShowId, Guid EventId)> AddShowAsync(string eventName, Guid? venueId, string showStatus = "Active", string eventStatus = "Published")
    {
        var events = new EventRepository(Connections());
        var evt = await events.CreateEventAsync(new Event
        {
            OrganizerId = Guid.NewGuid(),
            Name = eventName,
            Description = "d",
            Category = "Concert",
            EventDate = new DateOnly(2026, 12, 1),
            EventTime = new TimeOnly(19, 0),
            Status = "Published"
        });
        var show = await events.CreateShowWithCategoriesAsync(
            new Show { EventId = evt.Id, ShowDate = new DateOnly(2026, 12, 1), ShowTime = new TimeOnly(19, 0), VenueId = venueId, Status = showStatus },
            new List<TicketCategory> { new() { Name = "General", Price = 10, Capacity = 10 } });
        // The database refuses new shows on a cancelled event, so cancel it afterwards.
        if (eventStatus != "Published")
        {
            await events.UpdateEventStatusAsync(evt.Id, eventStatus);
        }

        return (show.Id, evt.Id);
    }

    [Fact]
    public async Task ReturnsEventVenueScheduleAndStatusForEachRequestedShow()
    {
        var venue = await AddVenueAsync("Nelum Pokuna");
        var (showId, _) = await AddShowAsync("Arijit Singh Live", venue);

        var info = Assert.Single(await new EventRepository(Connections()).GetShowReminderInfoAsync(new[] { showId }));

        Assert.Equal(("Arijit Singh Live", "Nelum Pokuna", false), (info.EventName, info.VenueName, info.Cancelled));
        Assert.Equal((new DateOnly(2026, 12, 1), new TimeOnly(19, 0)), (info.ShowDate, info.ShowTime));
    }

    [Fact]
    public async Task AShowWithoutAVenue_HasANullVenueName()
    {
        var (showId, _) = await AddShowAsync("Open Air", null);

        var info = Assert.Single(await new EventRepository(Connections()).GetShowReminderInfoAsync(new[] { showId }));

        Assert.Null(info.VenueName);
    }

    [Theory]
    [InlineData("Cancelled", "Published")]
    [InlineData("Active", "Cancelled")]
    public async Task ACancelledShowOrEvent_IsReportedCancelled(string showStatus, string eventStatus)
    {
        var (showId, _) = await AddShowAsync("Called Off", null, showStatus, eventStatus);

        var info = Assert.Single(await new EventRepository(Connections()).GetShowReminderInfoAsync(new[] { showId }));

        Assert.True(info.Cancelled);
    }

    [Fact]
    public async Task ManyShows_AreAnsweredInOneCall_AndUnknownIdsAreLeftOut()
    {
        var (a, _) = await AddShowAsync("A", null);
        var (b, _) = await AddShowAsync("B", null);

        var infos = await new EventRepository(Connections()).GetShowReminderInfoAsync(new[] { a, b, Guid.NewGuid() });

        Assert.Equal(new[] { "A", "B" }, infos.Select(i => i.EventName).Order());
    }

    [Fact]
    public async Task NoIds_ReturnsNothing() =>
        Assert.Empty(await new EventRepository(Connections()).GetShowReminderInfoAsync(Array.Empty<Guid>()));
}
