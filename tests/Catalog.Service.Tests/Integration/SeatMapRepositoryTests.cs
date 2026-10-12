using Catalog.Service.Db;
using Catalog.Service.Models;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Catalog.Service.Tests.Integration;

// Real PostgreSQL: request and layout state, one-pending-per-show, version numbering and the
// enable transaction under concurrency.
[Collection("Postgres")]
public sealed class SeatMapRepositoryTests
{
    private readonly PostgresFixture _db;

    public SeatMapRepositoryTests(PostgresFixture db) => _db = db;

    private DbConnectionFactory Connections() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString })
        .Build());

    private SeatMapRepository Repository() => new(Connections());

    private async Task<(Guid ShowId, Guid OrganizerId, Guid CategoryId)> SeedShowAsync()
    {
        var events = new EventRepository(Connections());
        var organizer = Guid.NewGuid();
        var evt = await events.CreateEventAsync(new Event
        {
            OrganizerId = organizer,
            Name = "Seat Test",
            Description = "d",
            Category = "Concert",
            EventDate = new DateOnly(2027, 1, 1),
            EventTime = new TimeOnly(18, 0),
            Status = "Draft"
        });
        var category = new TicketCategory { Name = "General", Price = 10, Capacity = 12 };
        var show = await events.CreateShowWithCategoriesAsync(
            new Show { EventId = evt.Id, ShowDate = new DateOnly(2027, 1, 1), ShowTime = new TimeOnly(18, 0) },
            new List<TicketCategory> { category });
        var categories = await events.GetTicketCategoriesByShowIdAsync(show.Id);
        return (show.Id, organizer, categories.Single().Id);
    }

    private static SeatMapRequest NewRequest(Guid showId, Guid organizerId) =>
        new(Guid.CreateVersion7(), showId, organizerId, "Two blocks of stalls", SeatMapStatuses.Pending, null, DateTimeOffset.UtcNow, null, null);

    private static SeatLayoutSpec Spec(Guid category, string section = "Stalls", int rows = 3, int perRow = 4) =>
        new(new[] { new SeatLayoutSection(section, category, rows, perRow) });

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }

    [Fact]
    public async Task NewShow_DefaultsToGeneralAdmission()
    {
        var (showId, _, _) = await SeedShowAsync();

        var show = await new EventRepository(Connections()).GetShowByIdAsync(showId);

        Assert.Equal(SeatingModes.GeneralAdmission, show!.SeatingMode);
    }

    [Fact]
    public async Task CreateRequest_StoresAPendingRequestTheOrganizerCanSee()
    {
        var (showId, organizerId, _) = await SeedShowAsync();
        var repository = Repository();
        var request = NewRequest(showId, organizerId);

        Assert.Equal(CreateSeatMapRequestOutcome.Created, await repository.CreateRequestAsync(request));

        var stored = (await repository.GetRequestsForShowAsync(showId)).Single();
        Assert.Equal(SeatMapStatuses.Pending, stored.Status);
        Assert.Equal(organizerId, stored.OrganizerId);
        Assert.Equal("Two blocks of stalls", stored.Description);
    }

    [Fact]
    public async Task CreateRequest_UnknownShow_IsNotFound() =>
        Assert.Equal(CreateSeatMapRequestOutcome.ShowNotFound, await Repository().CreateRequestAsync(NewRequest(Guid.NewGuid(), Guid.NewGuid())));

    [Fact]
    public async Task CreateRequest_WhileOneIsPending_IsRefused()
    {
        var (showId, organizerId, _) = await SeedShowAsync();
        var repository = Repository();
        await repository.CreateRequestAsync(NewRequest(showId, organizerId));

        Assert.Equal(CreateSeatMapRequestOutcome.PendingExists, await repository.CreateRequestAsync(NewRequest(showId, organizerId)));
    }

    [Fact]
    public async Task CreateRequest_ManyParallel_ExactlyOneIsStored()
    {
        var (showId, organizerId, _) = await SeedShowAsync();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => Repository().CreateRequestAsync(NewRequest(showId, organizerId))));

        Assert.Equal(1, outcomes.Count(o => o == CreateSeatMapRequestOutcome.Created));
        Assert.Equal(14, outcomes.Count(o => o == CreateSeatMapRequestOutcome.PendingExists));
        Assert.Single(await Repository().GetRequestsForShowAsync(showId));
    }

    [Fact]
    public async Task CreateRequest_AfterRejection_IsAllowedAgain()
    {
        var (showId, organizerId, _) = await SeedShowAsync();
        var repository = Repository();
        var first = NewRequest(showId, organizerId);
        await repository.CreateRequestAsync(first);
        await repository.RejectAsync(first.Id, "Venue plan missing", "admin", DateTimeOffset.UtcNow);

        Assert.Equal(CreateSeatMapRequestOutcome.Created, await repository.CreateRequestAsync(NewRequest(showId, organizerId)));
    }

    [Fact]
    public async Task Reject_RecordsTheReasonOnce()
    {
        var (showId, organizerId, _) = await SeedShowAsync();
        var repository = Repository();
        var request = NewRequest(showId, organizerId);
        await repository.CreateRequestAsync(request);

        Assert.Equal(RejectOutcome.Rejected, await repository.RejectAsync(request.Id, "Venue plan missing", "admin-1", DateTimeOffset.UtcNow));
        Assert.Equal(RejectOutcome.NotPending, await repository.RejectAsync(request.Id, "again", "admin-2", DateTimeOffset.UtcNow));
        Assert.Equal(RejectOutcome.NotFound, await repository.RejectAsync(Guid.NewGuid(), "x", "admin", DateTimeOffset.UtcNow));

        var stored = await repository.GetRequestAsync(request.Id);
        Assert.Equal(SeatMapStatuses.Rejected, stored!.Status);
        Assert.Equal("Venue plan missing", stored.RejectionReason);
        Assert.Equal("admin-1", stored.DecidedBy);
    }

    [Fact]
    public async Task CreateDraft_NumbersVersionsAndStoresTheLayoutAsData()
    {
        var (showId, organizerId, category) = await SeedShowAsync();
        var repository = Repository();
        var request = NewRequest(showId, organizerId);
        await repository.CreateRequestAsync(request);

        var (outcome, first) = await repository.CreateDraftAsync(request.Id, Spec(category), "admin", DateTimeOffset.UtcNow);
        var (_, second) = await repository.CreateDraftAsync(request.Id, Spec(category, "Balcony"), "admin", DateTimeOffset.UtcNow);

        Assert.Equal(CreateDraftOutcome.Created, outcome);
        Assert.Equal(new[] { 1, 2 }, new[] { first!.Version, second!.Version });
        var stored = await repository.GetLayoutAsync(first.Id);
        Assert.Equal(SeatLayoutStatuses.Draft, stored!.Status);
        Assert.Equal(12, stored.SeatCount);
        Assert.Equal(new SeatLayoutSection("Stalls", category, 3, 4), stored.Spec.Sections.Single());
    }

    [Fact]
    public async Task CreateDraft_ParallelDrafts_GetDistinctVersions()
    {
        var (showId, organizerId, category) = await SeedShowAsync();
        var repository = Repository();
        var request = NewRequest(showId, organizerId);
        await repository.CreateRequestAsync(request);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            Repository().CreateDraftAsync(request.Id, Spec(category, $"S{i}"), "admin", DateTimeOffset.UtcNow)));

        Assert.All(results, r => Assert.Equal(CreateDraftOutcome.Created, r.Outcome));
        Assert.Equal(Enumerable.Range(1, 10), results.Select(r => r.Layout!.Version).Order());
    }

    [Fact]
    public async Task CreateDraft_ForRejectedOrUnknownRequest_IsRefused()
    {
        var (showId, organizerId, category) = await SeedShowAsync();
        var repository = Repository();
        var request = NewRequest(showId, organizerId);
        await repository.CreateRequestAsync(request);
        await repository.RejectAsync(request.Id, "no", "admin", DateTimeOffset.UtcNow);

        Assert.Equal(CreateDraftOutcome.RequestNotPending, (await repository.CreateDraftAsync(request.Id, Spec(category), "admin", DateTimeOffset.UtcNow)).Outcome);
        Assert.Equal(CreateDraftOutcome.RequestNotFound, (await repository.CreateDraftAsync(Guid.NewGuid(), Spec(category), "admin", DateTimeOffset.UtcNow)).Outcome);
    }

    [Fact]
    public async Task CompleteEnable_CompletesTheRequestAndMakesTheShowSeated()
    {
        var (showId, organizerId, category) = await SeedShowAsync();
        var repository = Repository();
        var request = NewRequest(showId, organizerId);
        await repository.CreateRequestAsync(request);
        var layout = (await repository.CreateDraftAsync(request.Id, Spec(category), "admin", DateTimeOffset.UtcNow)).Layout!;

        var outcome = await repository.CompleteEnableAsync(layout.Id, "admin-1", DateTimeOffset.UtcNow);

        Assert.Equal(CompleteEnableOutcome.Enabled, outcome);
        Assert.Equal(SeatLayoutStatuses.Enabled, (await repository.GetLayoutAsync(layout.Id))!.Status);
        var completed = await repository.GetRequestAsync(request.Id);
        Assert.Equal(SeatMapStatuses.Completed, completed!.Status);
        Assert.Equal("admin-1", completed.DecidedBy);
        Assert.Equal(SeatingModes.Seated, (await new EventRepository(Connections()).GetShowByIdAsync(showId))!.SeatingMode);
        Assert.Equal(CreateSeatMapRequestOutcome.AlreadySeated, await repository.CreateRequestAsync(NewRequest(showId, organizerId)));
    }

    [Fact]
    public async Task CompleteEnable_ManyParallel_EnablesExactlyOnce()
    {
        var (showId, organizerId, category) = await SeedShowAsync();
        var repository = Repository();
        var request = NewRequest(showId, organizerId);
        await repository.CreateRequestAsync(request);
        var layout = (await repository.CreateDraftAsync(request.Id, Spec(category), "admin", DateTimeOffset.UtcNow)).Layout!;

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => Repository().CompleteEnableAsync(layout.Id, "admin", DateTimeOffset.UtcNow)));

        Assert.Equal(1, outcomes.Count(o => o == CompleteEnableOutcome.Enabled));
        Assert.Equal(14, outcomes.Count(o => o == CompleteEnableOutcome.AlreadyEnabled));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*)::int FROM seat_layouts WHERE show_id = @s AND status = 'Enabled'", ("s", showId)));
    }

    [Fact]
    public async Task CompleteEnable_TwoDraftsRacing_LeavesExactlyOneEnabled()
    {
        var (showId, organizerId, category) = await SeedShowAsync();
        var repository = Repository();
        var request = NewRequest(showId, organizerId);
        await repository.CreateRequestAsync(request);
        var one = (await repository.CreateDraftAsync(request.Id, Spec(category, "One"), "admin", DateTimeOffset.UtcNow)).Layout!;
        var two = (await repository.CreateDraftAsync(request.Id, Spec(category, "Two"), "admin", DateTimeOffset.UtcNow)).Layout!;

        var outcomes = await Task.WhenAll(
            Repository().CompleteEnableAsync(one.Id, "admin", DateTimeOffset.UtcNow),
            Repository().CompleteEnableAsync(two.Id, "admin", DateTimeOffset.UtcNow));

        // Either order is valid; the later enable supersedes the earlier one.
        Assert.All(outcomes, o => Assert.Equal(CompleteEnableOutcome.Enabled, o));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*)::int FROM seat_layouts WHERE show_id = @s AND status = 'Enabled'", ("s", showId)));
        Assert.Equal(1, await ScalarAsync<int>("SELECT count(*)::int FROM seat_layouts WHERE show_id = @s AND status = 'Superseded'", ("s", showId)));
    }

    [Fact]
    public async Task CompleteEnable_NewVersionOfACompletedRequest_SupersedesTheOldLayout()
    {
        var (showId, organizerId, category) = await SeedShowAsync();
        var repository = Repository();
        var request = NewRequest(showId, organizerId);
        await repository.CreateRequestAsync(request);
        var v1 = (await repository.CreateDraftAsync(request.Id, Spec(category, "One"), "admin", DateTimeOffset.UtcNow)).Layout!;
        await repository.CompleteEnableAsync(v1.Id, "admin", DateTimeOffset.UtcNow);
        var (draftOutcome, v2) = await repository.CreateDraftAsync(request.Id, Spec(category, "Two"), "admin", DateTimeOffset.UtcNow);

        var outcome = await repository.CompleteEnableAsync(v2!.Id, "admin", DateTimeOffset.UtcNow);

        Assert.Equal(CreateDraftOutcome.Created, draftOutcome);
        Assert.Equal(CompleteEnableOutcome.Enabled, outcome);
        Assert.Equal(SeatLayoutStatuses.Superseded, (await repository.GetLayoutAsync(v1.Id))!.Status);
        Assert.Equal(SeatLayoutStatuses.Enabled, (await repository.GetLayoutAsync(v2.Id))!.Status);
    }

    [Fact]
    public async Task CompleteEnable_OnARejectedRequest_IsRefused()
    {
        var (showId, organizerId, category) = await SeedShowAsync();
        var repository = Repository();
        var request = NewRequest(showId, organizerId);
        await repository.CreateRequestAsync(request);
        var layout = (await repository.CreateDraftAsync(request.Id, Spec(category), "admin", DateTimeOffset.UtcNow)).Layout!;
        await repository.RejectAsync(request.Id, "no", "admin", DateTimeOffset.UtcNow);

        Assert.Equal(CompleteEnableOutcome.NotEnableable, await repository.CompleteEnableAsync(layout.Id, "admin", DateTimeOffset.UtcNow));
        Assert.Equal(CompleteEnableOutcome.LayoutNotFound, await repository.CompleteEnableAsync(Guid.NewGuid(), "admin", DateTimeOffset.UtcNow));
        Assert.Equal(SeatingModes.GeneralAdmission, (await new EventRepository(Connections()).GetShowByIdAsync(showId))!.SeatingMode);
    }

    [Fact]
    public async Task List_FiltersByStatus()
    {
        var (showId, organizerId, _) = await SeedShowAsync();
        var repository = Repository();
        var request = NewRequest(showId, organizerId);
        await repository.CreateRequestAsync(request);

        var pending = (await repository.ListRequestsAsync(SeatMapStatuses.Pending, 500)).Single(r => r.Request.Id == request.Id);
        Assert.Equal("Seat Test", pending.EventName);
        Assert.Equal(12, pending.TicketTotal);
        Assert.DoesNotContain(await repository.ListRequestsAsync(SeatMapStatuses.Completed, 500), r => r.Request.Id == request.Id);
        Assert.Contains(await repository.ListRequestsAsync(null, 500), r => r.Request.Id == request.Id);
    }

    [Fact]
    public async Task DeleteEvent_WithSeatMapRequestsAndLayouts_RemovesThemWithTheShow()
    {
        var (showId, organizerId, category) = await SeedShowAsync();
        var repository = Repository();
        var request = NewRequest(showId, organizerId);
        await repository.CreateRequestAsync(request);
        await repository.CreateDraftAsync(request.Id, Spec(category), "admin", DateTimeOffset.UtcNow);
        var events = new EventRepository(Connections());
        var show = await events.GetShowByIdAsync(showId);

        await events.DeleteEventAsync(show!.EventId);

        Assert.Null(await events.GetShowByIdAsync(showId));
        Assert.Null(await repository.GetRequestAsync(request.Id));
        Assert.Empty(await repository.GetLayoutsForShowAsync(showId));
    }
}
