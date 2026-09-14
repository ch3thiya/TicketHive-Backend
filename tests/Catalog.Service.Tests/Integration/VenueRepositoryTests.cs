using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Catalog.Service.Db;
using Catalog.Service.Models;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Catalog.Service.Tests.Integration;

[Collection("Postgres")]
public sealed class VenueRepositoryTests
{
    private readonly PostgresFixture _db;

    public VenueRepositoryTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task CreateVenueAsync_ThenGetById_ReturnsSameVenue()
    {
        // Arrange
        var repository = CreateRepository();
        var now = DateTime.UtcNow;
        var venue = new Venue
        {
            Id = Guid.CreateVersion7(),
            Name = "Nelum Pokuna",
            Address = "Colombo 07",
            Capacity = 500,
            CreatedAt = now,
            UpdatedAt = now
        };

        // Act
        var created = await repository.CreateVenueAsync(venue);
        var fetched = await repository.GetVenueByIdAsync(created.Id);

        // Assert
        Assert.NotNull(fetched);
        Assert.Equal("Nelum Pokuna", fetched!.Name);
        Assert.Equal("Colombo 07", fetched.Address);
        Assert.Equal(500, fetched.Capacity);
        Assert.Equal(created.Id, fetched.Id);
    }

    [Fact]
    public async Task UpdateVenueAsync_ChangesFields_KeepsIdAndCreatedAtMovesUpdatedAt()
    {
        // Arrange
        var repository = CreateRepository();
        var venue = await SeedVenueAsync(repository);
        var originalCreatedAt = venue.CreatedAt;

        // Act
        venue.Name = "Updated Name";
        venue.Address = "Updated Address";
        venue.Capacity = 250;
        venue.UpdatedAt = DateTime.UtcNow.AddMinutes(5);
        await repository.UpdateVenueAsync(venue);
        var fetched = await repository.GetVenueByIdAsync(venue.Id);

        // Assert
        Assert.NotNull(fetched);
        Assert.Equal(venue.Id, fetched!.Id);
        Assert.Equal("Updated Name", fetched.Name);
        Assert.Equal("Updated Address", fetched.Address);
        Assert.Equal(250, fetched.Capacity);
        // Postgres timestamptz has microsecond precision; DateTime carries
        // ticks finer than that, so compare with a small tolerance.
        Assert.Equal(originalCreatedAt, fetched.CreatedAt, TimeSpan.FromMilliseconds(1));
        Assert.True(fetched.UpdatedAt > originalCreatedAt);
    }

    [Fact]
    public async Task CreateVenueAsync_NonPositiveCapacity_ViolatesCheckConstraint()
    {
        // Arrange
        var repository = CreateRepository();
        var now = DateTime.UtcNow;
        var venue = new Venue
        {
            Id = Guid.CreateVersion7(),
            Name = "Bad Venue",
            Address = "Nowhere",
            Capacity = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        // Act & Assert
        await Assert.ThrowsAsync<PostgresException>(() => repository.CreateVenueAsync(venue));
    }

    [Fact]
    public async Task DeleteVenueIfUnusedAsync_UnusedVenue_Deletes()
    {
        // Arrange
        var repository = CreateRepository();
        var venue = await SeedVenueAsync(repository);

        // Act
        var result = await repository.DeleteVenueIfUnusedAsync(venue.Id);

        // Assert
        Assert.NotNull(result);
        Assert.True(result!.Deleted);
        Assert.Null(await repository.GetVenueByIdAsync(venue.Id));
    }

    [Fact]
    public async Task DeleteVenueIfUnusedAsync_VenueReferencedByShow_IsRefusedAndBothUntouched()
    {
        // Arrange
        var repository = CreateRepository();
        var eventRepository = CreateEventRepository();
        var venue = await SeedVenueAsync(repository);
        var evt = await eventRepository.CreateEventAsync(new Event { OrganizerId = Guid.NewGuid(), Name = "Evt", Status = "Draft" });
        var show = new Show
        {
            EventId = evt.Id,
            ShowDate = new DateOnly(2026, 11, 1),
            ShowTime = new TimeOnly(19, 30),
            VenueId = venue.Id,
            Status = "Active"
        };
        var createdShow = await eventRepository.CreateShowWithCategoriesAsync(
            show, new List<TicketCategory> { new() { Name = "General", Price = 10m, Capacity = 10 } });

        // Act
        var result = await repository.DeleteVenueIfUnusedAsync(venue.Id);

        // Assert
        Assert.NotNull(result);
        Assert.False(result!.Deleted);
        Assert.Equal(1, result.ReferencingShowCount);
        Assert.NotNull(await repository.GetVenueByIdAsync(venue.Id));
        var fetchedShow = await eventRepository.GetShowByIdAsync(createdShow.Id);
        Assert.NotNull(fetchedShow);
        Assert.Equal(venue.Id, fetchedShow!.VenueId);
    }

    [Fact]
    public async Task DeleteVenueIfUnusedAsync_UnknownVenue_ReturnsNull()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var result = await repository.DeleteVenueIfUnusedAsync(Guid.CreateVersion7());

        // Assert
        Assert.Null(result);
    }

    // A delete and a show that starts referencing the same venue at the same
    // moment must never both "win": either the delete succeeds and the show
    // insert is rejected, or the show insert succeeds and the delete is
    // refused. The shows.venue_id foreign key is what makes this true — the
    // insert's FK check needs a FOR KEY SHARE lock on the venue row, which
    // conflicts with the delete's FOR UPDATE lock, so the two transactions
    // serialize on that row instead of both reading "no conflict yet".
    // Without the foreign key, this can leave a show pointing at a deleted
    // venue — see the recorded verification in the PR description.
    [Fact]
    public async Task DeleteVenueIfUnusedAsync_ConcurrentWithShowCreation_NeverLeavesDanglingReference()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            // Arrange
            var venueRepository = CreateRepository();
            var eventRepository = CreateEventRepository();
            var venue = await SeedVenueAsync(venueRepository);
            var evt = await eventRepository.CreateEventAsync(new Event { OrganizerId = Guid.NewGuid(), Name = "Race Event", Status = "Draft" });

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<VenueDeleteResult?> DeleteAsync()
            {
                await gate.Task;
                return await CreateRepository().DeleteVenueIfUnusedAsync(venue.Id);
            }

            async Task<Exception?> CreateShowReferencingVenueAsync()
            {
                await gate.Task;
                var show = new Show
                {
                    EventId = evt.Id,
                    ShowDate = new DateOnly(2026, 11, 1),
                    ShowTime = new TimeOnly(19, 30),
                    VenueId = venue.Id,
                    Status = "Active"
                };
                try
                {
                    await CreateEventRepository().CreateShowWithCategoriesAsync(
                        show, new List<TicketCategory> { new() { Name = "General", Price = 10m, Capacity = 10 } });
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }

            // Act — both start at the same moment.
            var deleteTask = DeleteAsync();
            var createTask = CreateShowReferencingVenueAsync();
            gate.SetResult();
            var deleteResult = await deleteTask;
            var createError = await createTask;

            // Assert — the core invariant: no show may reference a venue that
            // no longer exists.
            var venueStillExists = await venueRepository.GetVenueByIdAsync(venue.Id) != null;
            var referencingShowCount = await CountShowsForVenueAsync(venue.Id);
            if (!venueStillExists)
            {
                Assert.Equal(0, referencingShowCount);
            }

            Assert.NotNull(deleteResult);
            var deleteWonAndCreateRejected = deleteResult!.Deleted && createError is ArgumentException;
            var createWonAndDeleteRefused = !deleteResult.Deleted && createError is null;
            Assert.True(deleteWonAndCreateRejected || createWonAndDeleteRefused,
                $"Iteration {iteration}: unexpected outcome, deleteResult.Deleted={deleteResult.Deleted}, createError={createError}");
        }
    }

    private async Task<int> CountShowsForVenueAsync(Guid venueId)
    {
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();

        const string sql = "SELECT COUNT(*) FROM shows WHERE venue_id = @VenueId;";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("VenueId", venueId);

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<Venue> SeedVenueAsync(VenueRepository repository)
    {
        var now = DateTime.UtcNow;
        return await repository.CreateVenueAsync(new Venue
        {
            Id = Guid.CreateVersion7(),
            Name = "Seed Venue",
            Address = "1 Seed Street",
            Capacity = 100,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    private VenueRepository CreateRepository() => new(new DbConnectionFactory(BuildConfiguration()));

    private EventRepository CreateEventRepository() => new(new DbConnectionFactory(BuildConfiguration()));

    private IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString
            })
            .Build();
    }
}
