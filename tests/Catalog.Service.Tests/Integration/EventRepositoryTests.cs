using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Catalog.Service.Db;
using Catalog.Service.Models;
using Microsoft.Extensions.Configuration;
using Npgsql;
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

        var venueId = await SeedVenueAsync();
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

    [Fact]
    public async Task SaveTicketCategoriesAsync_NoChanges_KeepsCategoryIdsStable()
    {
        // Arrange
        var repository = CreateRepository();
        var show = await SeedShowAsync(repository, ("General", 25.00m, 100), ("VIP", 75.00m, 20));
        var beforeIds = (await repository.GetTicketCategoriesByShowIdAsync(show.Id)).Select(c => c.Id).OrderBy(id => id).ToList();

        // Act — apply the same unchanged category set twice, as a show edit would.
        var current = await repository.GetTicketCategoriesByShowIdAsync(show.Id);
        await repository.SaveTicketCategoriesAsync(show.Id, CloneWithSameValues(current));
        current = await repository.GetTicketCategoriesByShowIdAsync(show.Id);
        await repository.SaveTicketCategoriesAsync(show.Id, CloneWithSameValues(current));

        // Assert
        var afterIds = (await repository.GetTicketCategoriesByShowIdAsync(show.Id)).Select(c => c.Id).OrderBy(id => id).ToList();
        Assert.Equal(beforeIds, afterIds);
    }

    [Fact]
    public async Task SaveTicketCategoriesAsync_ChangedFields_UpdatesInPlaceKeepingIdAndCreatedAt()
    {
        // Arrange
        var repository = CreateRepository();
        var show = await SeedShowAsync(repository, ("General", 25.00m, 100));
        var original = (await repository.GetTicketCategoriesByShowIdAsync(show.Id)).Single();

        // Act
        await repository.SaveTicketCategoriesAsync(show.Id, new List<TicketCategory>
        {
            new() { Id = original.Id, Name = "General Admission", Price = 30.00m, Capacity = 150 }
        });

        // Assert
        var updated = (await repository.GetTicketCategoriesByShowIdAsync(show.Id)).Single();
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal(original.CreatedAt, updated.CreatedAt);
        Assert.Equal("General Admission", updated.Name);
        Assert.Equal(30.00m, updated.Price);
        Assert.Equal(150, updated.Capacity);
    }

    [Fact]
    public async Task SaveTicketCategoriesAsync_AddsCategory_InsertsNewRowKeepingExistingIds()
    {
        // Arrange
        var repository = CreateRepository();
        var show = await SeedShowAsync(repository, ("General", 25.00m, 100));
        var existing = (await repository.GetTicketCategoriesByShowIdAsync(show.Id)).Single();

        // Act
        await repository.SaveTicketCategoriesAsync(show.Id, new List<TicketCategory>
        {
            new() { Id = existing.Id, Name = existing.Name, Price = existing.Price, Capacity = existing.Capacity },
            new() { Name = "VIP", Price = 90.00m, Capacity = 10 }
        });

        // Assert
        var categories = await repository.GetTicketCategoriesByShowIdAsync(show.Id);
        Assert.Equal(2, categories.Count);
        Assert.Contains(categories, c => c.Id == existing.Id);
        var inserted = Assert.Single(categories, c => c.Id != existing.Id);
        Assert.NotEqual(Guid.Empty, inserted.Id);
        Assert.Equal("VIP", inserted.Name);
    }

    [Fact]
    public async Task SaveTicketCategoriesAsync_OmittedCategory_RetiresRowKeepingIdAndHidesFromListing()
    {
        // Arrange
        var repository = CreateRepository();
        var show = await SeedShowAsync(repository, ("General", 25.00m, 100), ("VIP", 75.00m, 20));
        var categories = await repository.GetTicketCategoriesByShowIdAsync(show.Id);
        var keep = categories.First();
        var retire = categories.Last();

        // Act — send only the category to keep; the other is implicitly removed.
        await repository.SaveTicketCategoriesAsync(show.Id, new List<TicketCategory>
        {
            new() { Id = keep.Id, Name = keep.Name, Price = keep.Price, Capacity = keep.Capacity }
        });

        // Assert — listing hides it, but the row survives with is_active = false.
        var listed = await repository.GetTicketCategoriesByShowIdAsync(show.Id);
        Assert.Single(listed);
        Assert.Equal(keep.Id, listed[0].Id);

        var (exists, isActive, name) = await ReadCategoryRawAsync(retire.Id);
        Assert.True(exists);
        Assert.False(isActive);
        Assert.Equal(retire.Name, name);
    }

    [Fact]
    public async Task SaveTicketCategoriesAsync_UnknownId_ThrowsAndWritesNothing()
    {
        // Arrange
        var repository = CreateRepository();
        var show = await SeedShowAsync(repository, ("General", 25.00m, 100));
        var existing = (await repository.GetTicketCategoriesByShowIdAsync(show.Id)).Single();
        var unknownId = Guid.CreateVersion7();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveTicketCategoriesAsync(show.Id, new List<TicketCategory>
        {
            new() { Id = existing.Id, Name = existing.Name, Price = existing.Price, Capacity = existing.Capacity },
            new() { Id = unknownId, Name = "Ghost", Price = 10m, Capacity = 5 }
        }));

        var categories = await repository.GetTicketCategoriesByShowIdAsync(show.Id);
        Assert.Single(categories);
        Assert.Equal(existing.Name, categories[0].Name);
    }

    [Fact]
    public async Task SaveTicketCategoriesAsync_IdFromAnotherShow_ThrowsAndWritesNothing()
    {
        // Arrange
        var repository = CreateRepository();
        var showA = await SeedShowAsync(repository, ("General", 25.00m, 100));
        var showB = await SeedShowAsync(repository, ("General", 40.00m, 50));
        var categoryFromA = (await repository.GetTicketCategoriesByShowIdAsync(showA.Id)).Single();
        var categoryFromB = (await repository.GetTicketCategoriesByShowIdAsync(showB.Id)).Single();

        // Act & Assert — showB's update tries to claim showA's category id.
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveTicketCategoriesAsync(showB.Id, new List<TicketCategory>
        {
            new() { Id = categoryFromA.Id, Name = "Hijacked", Price = 1m, Capacity = 1 }
        }));

        var stillA = await repository.GetTicketCategoriesByShowIdAsync(showA.Id);
        var stillB = await repository.GetTicketCategoriesByShowIdAsync(showB.Id);
        Assert.Equal(categoryFromA.Name, stillA.Single().Name);
        Assert.Equal(categoryFromB.Name, stillB.Single().Name);
    }

    [Fact]
    public async Task SaveTicketCategoriesAsync_RetiredId_ThrowsAndWritesNothing()
    {
        // Arrange
        var repository = CreateRepository();
        var show = await SeedShowAsync(repository, ("General", 25.00m, 100), ("VIP", 75.00m, 20));
        var categories = await repository.GetTicketCategoriesByShowIdAsync(show.Id);
        var keep = categories.First();
        var retired = categories.Last();

        // Retire it first.
        await repository.SaveTicketCategoriesAsync(show.Id, new List<TicketCategory>
        {
            new() { Id = keep.Id, Name = keep.Name, Price = keep.Price, Capacity = keep.Capacity }
        });

        // Act & Assert — sending the retired id again must be rejected.
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveTicketCategoriesAsync(show.Id, new List<TicketCategory>
        {
            new() { Id = keep.Id, Name = keep.Name, Price = keep.Price, Capacity = keep.Capacity },
            new() { Id = retired.Id, Name = "Reused", Price = 5m, Capacity = 5 }
        }));

        var (exists, isActive, name) = await ReadCategoryRawAsync(retired.Id);
        Assert.True(exists);
        Assert.False(isActive);
        Assert.Equal(retired.Name, name);
    }

    [Fact]
    public async Task SaveTicketCategoriesAsync_FailureMidway_LeavesAllCategoriesUnchanged()
    {
        // Arrange
        var repository = CreateRepository();
        var show = await SeedShowAsync(repository, ("General", 25.00m, 100));
        var existing = (await repository.GetTicketCategoriesByShowIdAsync(show.Id)).Single();
        var unknownId = Guid.CreateVersion7();

        // Act — a valid update paired with an invalid id in the same call.
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveTicketCategoriesAsync(show.Id, new List<TicketCategory>
        {
            new() { Id = existing.Id, Name = "Changed Name", Price = 999m, Capacity = 999 },
            new() { Id = unknownId, Name = "Ghost", Price = 10m, Capacity = 5 }
        }));

        // Assert — the whole transaction rolled back, so the valid update never applied either.
        var afterFailure = (await repository.GetTicketCategoriesByShowIdAsync(show.Id)).Single();
        Assert.Equal(existing.Name, afterFailure.Name);
        Assert.Equal(existing.Price, afterFailure.Price);
        Assert.Equal(existing.Capacity, afterFailure.Capacity);
    }

    // Two organizers editing the same show's categories at the same moment must
    // serialize on the FOR UPDATE lock, not interleave. Each call keeps Alpha
    // plus one of Bravo/Charlie and implicitly drops the other, so under any
    // correct serial ordering exactly one call loses (its kept id has just been
    // retired by the other) and the survivor's category is the one left active.
    // Without the lock, both calls can read the initial state before either
    // commits, and each independently retires the row it wasn't told to keep —
    // ending with BOTH Bravo and Charlie retired and neither call reporting an
    // error, which is not a state either serial ordering can produce.
    [Fact]
    public async Task SaveTicketCategoriesAsync_ConcurrentUpdatesToSameShow_AreSerialized()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            // Arrange
            var seedRepository = CreateRepository();
            var show = await SeedShowAsync(seedRepository, ("Alpha", 10.00m, 100), ("Bravo", 20.00m, 50), ("Charlie", 30.00m, 25));
            var categories = await seedRepository.GetTicketCategoriesByShowIdAsync(show.Id);
            var alpha = categories.Single(c => c.Name == "Alpha");
            var bravo = categories.Single(c => c.Name == "Bravo");
            var charlie = categories.Single(c => c.Name == "Charlie");

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<Exception?> KeepAlphaAndAsync(TicketCategory second)
            {
                await gate.Task;
                var repository = CreateRepository();
                try
                {
                    await repository.SaveTicketCategoriesAsync(show.Id, new List<TicketCategory>
                    {
                        new() { Id = alpha.Id, Name = alpha.Name, Price = alpha.Price, Capacity = alpha.Capacity },
                        new() { Id = second.Id, Name = second.Name, Price = second.Price, Capacity = second.Capacity }
                    });
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }

            // Act — both start at the same moment: one intends to keep Bravo (drop
            // Charlie), the other to keep Charlie (drop Bravo).
            var keepBravoTask = KeepAlphaAndAsync(bravo);
            var keepCharlieTask = KeepAlphaAndAsync(charlie);
            gate.SetResult();
            var results = await Task.WhenAll(keepBravoTask, keepCharlieTask);

            var keepBravoRejected = results[0] is ArgumentException;
            var keepCharlieRejected = results[1] is ArgumentException;

            // Assert
            Assert.True(keepBravoRejected ^ keepCharlieRejected,
                $"Iteration {iteration}: expected exactly one call to be rejected as stale, got keepBravo={results[0]}, keepCharlie={results[1]}.");

            var (_, alphaActive, _) = await ReadCategoryRawAsync(alpha.Id);
            var (_, bravoActive, _) = await ReadCategoryRawAsync(bravo.Id);
            var (_, charlieActive, _) = await ReadCategoryRawAsync(charlie.Id);

            Assert.True(alphaActive, $"Iteration {iteration}: Alpha was named by both calls and must remain active.");
            Assert.True(bravoActive ^ charlieActive,
                $"Iteration {iteration}: expected exactly one of Bravo/Charlie active, got Bravo={bravoActive}, Charlie={charlieActive}.");
            Assert.Equal(!keepBravoRejected, bravoActive);
            Assert.Equal(!keepCharlieRejected, charlieActive);
            Assert.Equal(3, await CountCategoryRowsAsync(show.Id));
        }
    }

    private static List<TicketCategory> CloneWithSameValues(List<TicketCategory> categories)
    {
        return categories.Select(c => new TicketCategory { Id = c.Id, Name = c.Name, Price = c.Price, Capacity = c.Capacity }).ToList();
    }

    private async Task<(bool Exists, bool IsActive, string Name)> ReadCategoryRawAsync(Guid categoryId)
    {
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();

        const string sql = "SELECT is_active, name FROM ticket_categories WHERE id = @Id;";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", categoryId);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return (true, reader.GetBoolean(0), reader.GetString(1));
        }

        return (false, false, string.Empty);
    }

    private async Task<long> CountCategoryRowsAsync(Guid showId)
    {
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();

        const string sql = "SELECT COUNT(*) FROM ticket_categories WHERE show_id = @ShowId;";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ShowId", showId);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<Show> SeedShowAsync(EventRepository repository, params (string Name, decimal Price, int Capacity)[] categories)
    {
        var evt = await repository.CreateEventAsync(new Event
        {
            OrganizerId = Guid.NewGuid(),
            Name = "Seed Event",
            Status = "Draft"
        });

        var show = new Show
        {
            EventId = evt.Id,
            ShowDate = new DateOnly(2026, 11, 1),
            ShowTime = new TimeOnly(19, 30),
            Status = "Active"
        };

        var domainCategories = categories.Select(c => new TicketCategory { Name = c.Name, Price = c.Price, Capacity = c.Capacity }).ToList();
        return await repository.CreateShowWithCategoriesAsync(show, domainCategories);
    }

    [Fact]
    public async Task GetShowsAndCategoriesByIds_ManyEventsShowsAndCategories_ReturnsSameDataWithConstantQueryCount()
    {
        // Arrange - seed a small batch first, then a larger batch, and prove
        // the batched calls issue the same fixed number of queries either
        // way (AC6). Query count is measured via an ActivityListener on
        // Npgsql's own "Npgsql" ActivitySource: Npgsql emits one Activity
        // per executed command whenever a listener is attached, so this
        // counts real round trips without any production-code change or
        // custom counting wrapper.
        var repository = CreateRepository();
        var organizerId = Guid.NewGuid();

        var smallBatch = await SeedEventsWithShowsAndCategoriesAsync(repository, organizerId, eventCount: 3, showsPerEvent: 2, categoriesPerShow: 2);
        var smallBatchQueryCount = await CountNpgsqlQueriesAsync(async () =>
        {
            var (showsByEventId, categoriesByShowId) = await FetchShowsAndCategoriesAsync(repository, smallBatch.Events);
            AssertMatchesSeededShape(smallBatch, showsByEventId, categoriesByShowId);
        });

        var largeBatch = await SeedEventsWithShowsAndCategoriesAsync(repository, organizerId, eventCount: 10, showsPerEvent: 3, categoriesPerShow: 2);
        var largeBatchQueryCount = await CountNpgsqlQueriesAsync(async () =>
        {
            var (showsByEventId, categoriesByShowId) = await FetchShowsAndCategoriesAsync(repository, largeBatch.Events);
            AssertMatchesSeededShape(largeBatch, showsByEventId, categoriesByShowId);
        });

        // Assert - two queries (one for shows, one for categories) no matter
        // how many events/shows/categories were fetched.
        Assert.Equal(2, smallBatchQueryCount);
        Assert.Equal(2, largeBatchQueryCount);
    }

    private sealed record SeededBatch(List<Event> Events, Dictionary<Guid, List<Show>> ShowsByEventId, Dictionary<Guid, TicketCategory> ActiveCategoryByShowId, Guid RetiredCategoryId, Guid RetiredCategoryShowId);

    private static async Task<SeededBatch> SeedEventsWithShowsAndCategoriesAsync(
        EventRepository repository, Guid organizerId, int eventCount, int showsPerEvent, int categoriesPerShow)
    {
        var events = new List<Event>();
        var showsByEventId = new Dictionary<Guid, List<Show>>();
        var activeCategoryByShowId = new Dictionary<Guid, TicketCategory>();
        var retiredCategoryId = Guid.Empty;
        var retiredCategoryShowId = Guid.Empty;

        for (var e = 0; e < eventCount; e++)
        {
            var evt = await repository.CreateEventAsync(new Event
            {
                OrganizerId = organizerId,
                Name = $"Batch Event {Guid.NewGuid()}",
                Status = "Published"
            });
            events.Add(evt);
            showsByEventId[evt.Id] = new List<Show>();

            for (var s = 0; s < showsPerEvent; s++)
            {
                var show = new Show
                {
                    EventId = evt.Id,
                    ShowDate = new DateOnly(2026, 11, 1 + s),
                    ShowTime = new TimeOnly(19, 0),
                    Status = "Active"
                };
                var categories = new List<TicketCategory>();
                for (var c = 0; c < categoriesPerShow; c++)
                {
                    categories.Add(new TicketCategory { Name = $"Category {c}", Price = 10m + c, Capacity = 50 });
                }

                await repository.CreateShowWithCategoriesAsync(show, categories);
                showsByEventId[evt.Id].Add(show);
                activeCategoryByShowId[show.Id] = categories[0];

                if (retiredCategoryId == Guid.Empty && categories.Count > 1)
                {
                    // Retire the second category of the very first show seeded, so the
                    // batched fetch is proven to exclude it exactly like the
                    // single-show path does.
                    var keepOnly = new List<TicketCategory>
                    {
                        new() { Id = categories[0].Id, Name = categories[0].Name, Price = categories[0].Price, Capacity = categories[0].Capacity }
                    };
                    await repository.SaveTicketCategoriesAsync(show.Id, keepOnly);
                    retiredCategoryId = categories[1].Id;
                    retiredCategoryShowId = show.Id;
                }
            }
        }

        return new SeededBatch(events, showsByEventId, activeCategoryByShowId, retiredCategoryId, retiredCategoryShowId);
    }

    private static async Task<(Dictionary<Guid, List<Show>> ShowsByEventId, Dictionary<Guid, List<TicketCategory>> CategoriesByShowId)> FetchShowsAndCategoriesAsync(
        EventRepository repository, List<Event> events)
    {
        var eventIds = events.Select(e => e.Id).ToArray();
        var showsByEventId = await repository.GetShowsByEventIdsAsync(eventIds);

        var showIds = showsByEventId.Values.SelectMany(shows => shows).Select(s => s.Id).ToArray();
        var categoriesByShowId = await repository.GetTicketCategoriesByShowIdsAsync(showIds);

        return (showsByEventId, categoriesByShowId);
    }

    private static void AssertMatchesSeededShape(
        SeededBatch batch,
        Dictionary<Guid, List<Show>> showsByEventId,
        Dictionary<Guid, List<TicketCategory>> categoriesByShowId)
    {
        Assert.Equal(batch.Events.Count, showsByEventId.Count);
        foreach (var evt in batch.Events)
        {
            var expectedShows = batch.ShowsByEventId[evt.Id];
            var actualShows = showsByEventId[evt.Id];
            Assert.Equal(expectedShows.Select(s => s.Id).OrderBy(id => id), actualShows.Select(s => s.Id).OrderBy(id => id));

            foreach (var show in expectedShows)
            {
                var categories = categoriesByShowId.GetValueOrDefault(show.Id, new List<TicketCategory>());
                Assert.Contains(categories, c => c.Id == batch.ActiveCategoryByShowId[show.Id].Id);
                Assert.All(categories, c => Assert.True(c.IsActive));
            }
        }

        if (batch.RetiredCategoryId != Guid.Empty)
        {
            var categoriesOnRetiredShow = categoriesByShowId.GetValueOrDefault(batch.RetiredCategoryShowId, new List<TicketCategory>());
            Assert.DoesNotContain(categoriesOnRetiredShow, c => c.Id == batch.RetiredCategoryId);
        }
    }

    private static async Task<int> CountNpgsqlQueriesAsync(Func<Task> action)
    {
        var queryCount = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => Interlocked.Increment(ref queryCount)
        };
        ActivitySource.AddActivityListener(listener);

        await action();

        return queryCount;
    }

    // shows.venue_id now has a foreign key to venues, so any test exercising
    // it needs a real venue row rather than a fabricated GUID.
    private async Task<Guid> SeedVenueAsync()
    {
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();

        var venueId = Guid.CreateVersion7();
        const string sql = @"
            INSERT INTO venues (id, name, address, capacity, created_at, updated_at)
            VALUES (@Id, 'Seed Venue', '1 Seed Street', 100, now(), now());
        ";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", venueId);
        await command.ExecuteNonQueryAsync();

        return venueId;
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
