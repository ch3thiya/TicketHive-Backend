using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Notification.Service.Db;
using Notification.Service.Models;
using Xunit;

namespace Notification.Service.Tests.Integration;

// Real PostgreSQL: one reminder per order, claimed by exactly one instance, never sent twice.
[Collection("Postgres")]
public sealed class EventReminderRepositoryTests
{
    private readonly PostgresFixture _db;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));

    public EventReminderRepositoryTests(PostgresFixture db) => _db = db;

    private EventReminderRepository Repository() => new(new DbConnectionFactory(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString })
        .Build()), _clock);

    private static ReminderSeed Seed(Guid? show = null) =>
        new(Guid.CreateVersion7(), show ?? Guid.CreateVersion7(), "buyer@example.com", "Buyer", 2);

    private async Task<Guid> PendingAsync(EventReminderRepository repository)
    {
        var seed = Seed();
        Assert.True(await repository.EnsureAsync(seed, true));
        return seed.OrderId;
    }

    [Fact]
    public async Task TheSameOrderIsRecordedOnlyOnce_HoweverOftenItsEventArrives()
    {
        var repository = Repository();
        var seed = Seed();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => repository.EnsureAsync(seed, true)));

        Assert.Equal(1, results.Count(created => created));
        var record = await repository.GetAsync(seed.OrderId);
        Assert.Equal((ReminderStatuses.Pending, 0), (record!.Status, record.Attempts));
    }

    [Fact]
    public async Task ARedeliveredEvent_NeverReopensAFinishedReminder()
    {
        var repository = Repository();
        var seed = Seed();
        await repository.EnsureAsync(seed, true);
        await repository.ClaimDueAsync(10, 5);
        await repository.CompleteAsync(seed.OrderId, ReminderStatuses.Simulated);

        Assert.False(await repository.EnsureAsync(seed, true));

        Assert.Equal(ReminderStatuses.Simulated, (await repository.GetAsync(seed.OrderId))!.Status);
    }

    [Fact]
    public async Task AnUndeliverableAddress_IsRecordedButNeverClaimed()
    {
        var repository = Repository();
        var seed = Seed();
        await repository.EnsureAsync(seed with { RecipientEmail = "customer@tickethive.lk" }, false);

        var claimed = await repository.ClaimDueAsync(100, 5);

        Assert.DoesNotContain(claimed, r => r.OrderId == seed.OrderId);
        Assert.Equal(ReminderStatuses.MissingRecipient, (await repository.GetAsync(seed.OrderId))!.Status);
    }

    [Fact]
    public async Task ManyInstancesClaimingAtOnce_EachGetADifferentSetAndNothingIsClaimedTwice()
    {
        var repository = Repository();
        var show = Guid.CreateVersion7();
        var ids = new List<Guid>();
        for (var i = 0; i < 40; i++)
        {
            var seed = Seed(show);
            await repository.EnsureAsync(seed, true);
            ids.Add(seed.OrderId);
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Repository().ClaimDueAsync(7, 5)));

        var claimedIds = claims.SelectMany(c => c).Where(r => r.ShowId == show).Select(r => r.OrderId).ToList();
        Assert.Equal(claimedIds.Count, claimedIds.Distinct().Count());
        Assert.All(claimedIds, id => Assert.Contains(id, ids));
        Assert.All(claims, c => Assert.True(c.Count <= 7));
    }

    [Fact]
    public async Task ARemindersNotDueYet_IsNotClaimed_AndIsOnceTimePasses()
    {
        var repository = Repository();
        var id = await PendingAsync(repository);
        await repository.ClaimDueAsync(100, 5);
        await repository.DeferAsync(new[] { id }, _clock.GetUtcNow().AddHours(3));

        Assert.DoesNotContain(await repository.ClaimDueAsync(100, 5), r => r.OrderId == id);

        _clock.Advance(TimeSpan.FromHours(3));
        Assert.Contains(await repository.ClaimDueAsync(100, 5), r => r.OrderId == id);
    }

    [Fact]
    public async Task ADeferralDoesNotCountAsAnAttempt()
    {
        var repository = Repository();
        var id = await PendingAsync(repository);
        await repository.ClaimDueAsync(100, 5);

        await repository.DeferAsync(new[] { id }, _clock.GetUtcNow().AddMinutes(1));

        var record = await repository.GetAsync(id);
        Assert.Equal((ReminderStatuses.Pending, 0), (record!.Status, record.Attempts));
    }

    [Fact]
    public async Task ASendThatNeverFinished_IsParkedForPeople_AndNeverClaimedAgain()
    {
        var repository = Repository();
        var id = await PendingAsync(repository);
        Assert.Contains(await repository.ClaimDueAsync(100, 5), r => r.OrderId == id);

        _clock.Advance(TimeSpan.FromMinutes(6));
        var later = await repository.ClaimDueAsync(100, 5);

        Assert.DoesNotContain(later, r => r.OrderId == id);
        var record = await repository.GetAsync(id);
        Assert.Equal(ReminderStatuses.NeedsReconciliation, record!.Status);
        _clock.Advance(TimeSpan.FromDays(2));
        Assert.DoesNotContain(await repository.ClaimDueAsync(100, 5), r => r.OrderId == id);
    }

    [Fact]
    public async Task ASendStillWithinItsGraceTime_IsNotParked()
    {
        var repository = Repository();
        var id = await PendingAsync(repository);
        await repository.ClaimDueAsync(100, 5);

        _clock.Advance(TimeSpan.FromMinutes(4));
        await repository.ClaimDueAsync(100, 5);

        Assert.Equal(ReminderStatuses.Sending, (await repository.GetAsync(id))!.Status);
    }

    [Fact]
    public async Task ARefusedSendIsRetriedAfterTheBackoff_UntilTheAttemptsAreUsedUp()
    {
        var repository = Repository();
        var id = await PendingAsync(repository);
        var statuses = new List<string>();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Contains(await repository.ClaimDueAsync(100, 5), r => r.OrderId == id);
            statuses.Add(await repository.RecordFailureAsync(id, "The email provider refused the reminder (HTTP 500).", 3, _clock.GetUtcNow().AddMinutes(10)));
            if (attempt < 3)
            {
                Assert.DoesNotContain(await repository.ClaimDueAsync(100, 5), r => r.OrderId == id);
                _clock.Advance(TimeSpan.FromMinutes(10));
            }
        }

        Assert.Equal(new[] { ReminderStatuses.Pending, ReminderStatuses.Pending, ReminderStatuses.Failed }, statuses);
        var record = await repository.GetAsync(id);
        Assert.Equal((ReminderStatuses.Failed, 3), (record!.Status, record.Attempts));
        Assert.Contains("HTTP 500", record.LastError);
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.DoesNotContain(await repository.ClaimDueAsync(100, 5), r => r.OrderId == id);
    }

    [Fact]
    public async Task OnlyAClaimedReminderCanBeFinished_SoAStaleWorkerCannotOverwriteTheOutcome()
    {
        var repository = Repository();
        var id = await PendingAsync(repository);
        await repository.ClaimDueAsync(100, 5);
        await repository.CompleteAsync(id, ReminderStatuses.Simulated);

        await repository.CompleteAsync(id, ReminderStatuses.Sent);
        await repository.SkipAsync(id, ReminderSkipReasons.ShowCancelled);
        await repository.MarkNeedsReconciliationAsync(id, "late");
        var again = await repository.RecordFailureAsync(id, "late", 3, _clock.GetUtcNow());

        var record = await repository.GetAsync(id);
        Assert.Equal(ReminderStatuses.Simulated, record!.Status);
        Assert.Null(again);
        Assert.Equal(0, record.Attempts);
        Assert.Null(record.SkipReason);
    }

    [Fact]
    public async Task AFinishedReminderRecordsWhenItWentOut()
    {
        var repository = Repository();
        var id = await PendingAsync(repository);
        await repository.ClaimDueAsync(100, 5);

        await repository.CompleteAsync(id, ReminderStatuses.Sent);

        var record = await repository.GetAsync(id);
        Assert.Equal(_clock.GetUtcNow(), record!.SentAt);
    }
}
