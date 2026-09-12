using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Catalog.Service.Db;
using Catalog.Service.Models;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Catalog.Service.Tests.Integration;

[Collection("Postgres")]
public sealed class EventRepositoryTests
{
    private readonly PostgresFixture _db;

    public EventRepositoryTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task CreateEventAsync_ThenGetById_ReturnsSameEvent()
    {
        // Arrange
        var repository = CreateRepository();
        var organizerId = Guid.NewGuid();
        var evt = new Event
        {
            OrganizerId = organizerId,
            Name = "Neon Summer Concert",
            Description = "Live music event",
            Category = "Concert",
            EventDate = new DateOnly(2026, 9, 15),
            EventTime = new TimeOnly(18, 0),
            BannerUrl = "http://example.com/banner.jpg",
            Status = "Draft",
            CancellationCutoffHours = 48
        };

        // Act
        var created = await repository.CreateEventAsync(evt);
        var fetched = await repository.GetEventByIdAsync(created.Id);

        // Assert
        Assert.NotNull(fetched);
        Assert.Equal(organizerId, fetched!.OrganizerId);
        Assert.Equal("Neon Summer Concert", fetched.Name);
        Assert.Equal("Draft", fetched.Status);
        Assert.Equal(48, fetched.CancellationCutoffHours);
    }

    [Fact]
    public async Task CreateShowWithCategoriesAsync_ThenGetShowAndCategories_ReturnsColumnsAddedByAlter()
    {
        // Arrange
        var repository = CreateRepository();
        var evt = await repository.CreateEventAsync(new Event
        {
            OrganizerId = Guid.NewGuid(),
            Name = "Winter Jazz Night",
            Status = "Draft"
        });

        var venueId = Guid.NewGuid();
        var onSaleAt = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        var show = new Show
        {
            EventId = evt.Id,
            ShowDate = new DateOnly(2026, 11, 1),
            ShowTime = new TimeOnly(19, 30),
            VenueId = venueId,
            OnSaleAt = onSaleAt,
            HighDemandThreshold = 500,
            ReminderMinutesBefore = 60,
            Status = "Active"
        };
        var categories = new List<TicketCategory>
        {
            new() { Name = "General", Price = 25.00m, Capacity = 100 }
        };

        // Act
        var createdShow = await repository.CreateShowWithCategoriesAsync(show, categories);
        var fetchedShow = await repository.GetShowByIdAsync(createdShow.Id);
        var fetchedCategories = await repository.GetTicketCategoriesByShowIdAsync(createdShow.Id);

        // Assert — venue_id, on_sale_at, high_demand_threshold and reminder_minutes_before
        // are the columns the migration script's appended ALTER TABLE statements add.
        Assert.NotNull(fetchedShow);
        Assert.Equal(venueId, fetchedShow!.VenueId);
        Assert.Equal(onSaleAt, fetchedShow.OnSaleAt);
        Assert.Equal(500, fetchedShow.HighDemandThreshold);
        Assert.Equal(60, fetchedShow.ReminderMinutesBefore);
        Assert.Single(fetchedCategories);
        Assert.Equal("General", fetchedCategories[0].Name);
        Assert.Equal(25.00m, fetchedCategories[0].Price);
    }

    private EventRepository CreateRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString
            })
            .Build();

        return new EventRepository(new DbConnectionFactory(configuration));
    }
}
