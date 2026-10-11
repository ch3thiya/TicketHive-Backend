using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Notification.Service.Clients;
using Notification.Service.Db;
using Notification.Service.Models;
using Notification.Service.Services;
using Xunit;

namespace Notification.Service.Tests.Integration;

// The whole reminder pass against real PostgreSQL with fake Catalog, Booking and email provider.
// Nothing here can reach a real email provider or another service.
[Collection("Postgres")]
public sealed class ReminderProcessorTests
{
    private sealed class FakeCatalog : IReminderCatalogClient
    {
        public Dictionary<Guid, ReminderShow> Shows { get; } = new();
        public bool Down { get; set; }
        public int Calls { get; private set; }

        public Task<IReadOnlyDictionary<Guid, ReminderShow>?> GetShowsAsync(IReadOnlyCollection<Guid> showIds, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyDictionary<Guid, ReminderShow>?>(Down ? null : Shows.Where(s => showIds.Contains(s.Key)).ToDictionary(s => s.Key, s => s.Value));
        }
    }

    private sealed class FakeBooking : IReminderBookingClient
    {
        public Dictionary<Guid, int> Eligible { get; } = new();
        public bool Down { get; set; }
        public int Calls { get; private set; }

        public Task<IReadOnlyDictionary<Guid, int>?> GetEligibleOrdersAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyDictionary<Guid, int>?>(Down ? null : Eligible.Where(o => orderIds.Contains(o.Key)).ToDictionary(o => o.Key, o => o.Value));
        }
    }

    private sealed class FakeSender : IReminderEmailSender
    {
        public ConcurrentQueue<ReminderEmail> Sent { get; } = new();
        public ReminderSendResult Result { get; set; } = new(ReminderSendOutcome.Simulated);
        public bool Throws { get; set; }

        public Task<ReminderSendResult> SendAsync(ReminderEmail email, CancellationToken cancellationToken = default)
        {
            if (Throws) throw new InvalidOperationException("provider exploded");
            Sent.Enqueue(email);
            return Task.FromResult(Result);
        }
    }

    private readonly PostgresFixture _db;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 12, 1, 6, 0, 0, TimeSpan.Zero));
    private readonly FakeCatalog _catalog = new();
    private readonly FakeBooking _booking = new();
    private readonly FakeSender _sender = new();
    private readonly Meter _meter = new("reminder-tests");
    private readonly List<(string Outcome, long Value)> _outcomes = new();

    public ReminderProcessorTests(PostgresFixture db)
    {
        _db = db;
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == _meter) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "notification.reminders.outcomes")
            {
                lock (_outcomes) _outcomes.Add((tags.ToArray().Single(t => t.Key == "outcome").Value!.ToString()!, value));
            }
        });
        listener.Start();
    }

    private EventReminderRepository Repository() => new(new DbConnectionFactory(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString })
        .Build()), _clock);

    private ReminderProcessor Processor(ReminderOptions? options = null) =>
        new(Repository(), _catalog, _booking, _sender, Options.Create(options ?? new ReminderOptions()), _clock, new ReminderMetrics(_meter), NullLogger<ReminderProcessor>.Instance);

    // A show starting `hoursAway` hours from the fake clock, with one reminder for one order on it.
    private async Task<(Guid Show, Guid Order)> OrderAsync(double hoursAway, int tickets = 2, bool eligible = true, string email = "buyer@example.com", string? venue = "Nelum Pokuna")
    {
        var show = Guid.CreateVersion7();
        _catalog.Shows[show] = new ReminderShow(show, _clock.GetUtcNow().AddHours(hoursAway), "Arijit Singh Live", venue, false);
        return (show, await AddOrderOnAsync(show, tickets, eligible, email));
    }

    private async Task<Guid> AddOrderOnAsync(Guid show, int tickets = 2, bool eligible = true, string email = "buyer@example.com")
    {
        var order = Guid.CreateVersion7();
        await Repository().EnsureAsync(new ReminderSeed(order, show, email, "Bea Buyer", tickets), ReminderRules.IsDeliverableRecipient(email));
        if (eligible) _booking.Eligible[order] = tickets;
        return order;
    }

    private async Task<ReminderRecord> RecordAsync(Guid order) => (await Repository().GetAsync(order))!;

    private int SentTo(Guid order) => _sender.Sent.Count(e => e.Message.Contains(order.ToString()) || false);

    // ---- AC1 / AC2: a reminder inside the window, with the essentials ----

    [Fact]
    public async Task ADueOrderGetsOneReminder_WithTheEventDateTimeVenueAndTicketCount()
    {
        var (_, order) = await OrderAsync(hoursAway: 20, tickets: 2);
        _booking.Eligible[order] = 3; // live count from Booking wins over the count recorded earlier

        await Processor().RunOnceAsync();

        var email = Assert.Single(_sender.Sent, e => e.RecipientEmail == "buyer@example.com" && e.TicketCount == 3);
        Assert.Contains("Arijit Singh Live", email.Message);
        Assert.Contains("Nelum Pokuna", email.Message);
        Assert.Contains("(Sri Lanka time)", email.Message);
        Assert.Contains("3 tickets", email.Message);
        var record = await RecordAsync(order);
        Assert.Equal(ReminderStatuses.Simulated, record.Status);
        Assert.NotNull(record.SentAt);
    }

    [Theory]
    [InlineData(24.0)]
    [InlineData(23.5)]
    [InlineData(2.0)]
    [InlineData(0.05)]
    public async Task AnyOrderForAShowStartingWithinTheWindowIsReminded_IncludingLateBuyers(double hoursAway)
    {
        var (_, order) = await OrderAsync(hoursAway);

        await Processor().RunOnceAsync();

        Assert.Equal(ReminderStatuses.Simulated, (await RecordAsync(order)).Status);
    }

    [Fact]
    public async Task AShowBeyondTheWindow_IsLeftAloneUntilItsWindowOpens_ThenRemindedOnce()
    {
        var (_, order) = await OrderAsync(hoursAway: 30);
        var processor = Processor();

        await processor.RunOnceAsync();

        Assert.Empty(_sender.Sent);
        Assert.Equal(0, _booking.Calls);
        var waiting = await RecordAsync(order);
        Assert.Equal((ReminderStatuses.Pending, 0), (waiting.Status, waiting.Attempts));
        Assert.Equal(_clock.GetUtcNow().AddHours(6), waiting.NextAttemptAt); // 30h away: window opens in 6h

        await processor.RunOnceAsync();
        Assert.Empty(_sender.Sent);

        _clock.Advance(TimeSpan.FromHours(6));
        await processor.RunOnceAsync();

        Assert.Single(_sender.Sent);
        Assert.Equal(ReminderStatuses.Simulated, (await RecordAsync(order)).Status);
    }

    [Fact]
    public async Task ADistantShow_IsLookedAtAgainOnTheCap_InCaseItWasRescheduledEarlier()
    {
        var (show, order) = await OrderAsync(hoursAway: 200);
        await Processor().RunOnceAsync();
        Assert.Equal(_clock.GetUtcNow().AddHours(6), (await RecordAsync(order)).NextAttemptAt);

        // The organizer moves the show to tomorrow; the next look picks it up.
        _catalog.Shows[show] = _catalog.Shows[show] with { StartsAtUtc = _clock.GetUtcNow().AddHours(30).AddHours(6) };
        _clock.Advance(TimeSpan.FromHours(6));
        await Processor().RunOnceAsync();
        Assert.Empty(_sender.Sent);
        _catalog.Shows[show] = _catalog.Shows[show] with { StartsAtUtc = _clock.GetUtcNow().AddHours(10) };
        _clock.Advance(TimeSpan.FromHours(6));

        await Processor().RunOnceAsync();

        Assert.Equal(ReminderStatuses.Simulated, (await RecordAsync(order)).Status);
    }

    // ---- AC3: never twice ----

    [Fact]
    public async Task RunningTheJobAgainAndAgain_NeverRemindsTheSameOrderTwice()
    {
        var (_, order) = await OrderAsync(hoursAway: 10);

        for (var run = 0; run < 5; run++)
        {
            await Processor().RunOnceAsync();
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Single(_sender.Sent);
        Assert.Equal(ReminderStatuses.Simulated, (await RecordAsync(order)).Status);
    }

    [Fact]
    public async Task ManyInstancesRunningTheJobAtOnce_SendExactlyOneReminderPerOrder()
    {
        var show = Guid.CreateVersion7();
        _catalog.Shows[show] = new ReminderShow(show, _clock.GetUtcNow().AddHours(5), "Busy Night", "Hall", false);
        var orders = new List<Guid>();
        for (var i = 0; i < 60; i++) orders.Add(await AddOrderOnAsync(show, 1, true, $"buyer{i}@example.com"));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var pass = 0; pass < 4; pass++) await Processor(new ReminderOptions { BatchSize = 10 }).RunOnceAsync();
        })));

        var recipients = _sender.Sent.Where(e => e.EventName == "Busy Night").Select(e => e.RecipientEmail).ToList();
        Assert.Equal(60, recipients.Count);
        Assert.Equal(60, recipients.Distinct().Count());
        foreach (var order in orders) Assert.Equal(ReminderStatuses.Simulated, (await RecordAsync(order)).Status);
    }

    // ---- AC4: cancelled orders and shows ----

    [Fact]
    public async Task ACancelledOrder_GetsNoReminder()
    {
        var (_, order) = await OrderAsync(hoursAway: 10, eligible: false);

        await Processor().RunOnceAsync();

        Assert.Empty(_sender.Sent);
        var record = await RecordAsync(order);
        Assert.Equal((ReminderStatuses.Skipped, ReminderSkipReasons.OrderNotEligible), (record.Status, record.SkipReason));
    }

    [Fact]
    public async Task ACancelledShow_GetsNoReminders_AndBookingIsNeverAsked()
    {
        var (show, order) = await OrderAsync(hoursAway: 10);
        _catalog.Shows[show] = _catalog.Shows[show] with { Cancelled = true };

        await Processor().RunOnceAsync();

        Assert.Empty(_sender.Sent);
        Assert.Equal(0, _booking.Calls);
        Assert.Equal(ReminderSkipReasons.ShowCancelled, (await RecordAsync(order)).SkipReason);
    }

    [Fact]
    public async Task OrderCancelledAfterBeingRecorded_IsCaughtBecauseBookingIsAskedAtSendTime()
    {
        var (_, order) = await OrderAsync(hoursAway: 10);
        _booking.Eligible.Remove(order); // cancelled and refunded since the tickets were issued

        await Processor().RunOnceAsync();

        Assert.Empty(_sender.Sent);
        Assert.Equal(ReminderStatuses.Skipped, (await RecordAsync(order)).Status);
    }

    [Fact]
    public async Task AShowThatHasStarted_OrIsUnknown_GetsNoReminder()
    {
        var (_, started) = await OrderAsync(hoursAway: -1);
        var unknownShow = Guid.CreateVersion7();
        var unknown = await AddOrderOnAsync(unknownShow);

        await Processor().RunOnceAsync();

        Assert.Empty(_sender.Sent);
        Assert.Equal(ReminderSkipReasons.ShowStarted, (await RecordAsync(started)).SkipReason);
        Assert.Equal(ReminderSkipReasons.ShowNotFound, (await RecordAsync(unknown)).SkipReason);
    }

    [Theory]
    [InlineData("customer@tickethive.lk")]
    [InlineData("not-an-email")]
    [InlineData("")]
    public async Task APlaceholderOrMissingAddress_IsNeverSentTo(string email)
    {
        var (_, order) = await OrderAsync(hoursAway: 10, email: email);

        await Processor().RunOnceAsync();

        Assert.Empty(_sender.Sent);
        Assert.Equal(ReminderStatuses.MissingRecipient, (await RecordAsync(order)).Status);
    }

    // ---- fail closed ----

    [Fact]
    public async Task WhenCatalogIsDown_NothingIsSent_AndTheRemindersWaitForTheNextPass()
    {
        var (_, order) = await OrderAsync(hoursAway: 10);
        _catalog.Down = true;

        await Processor().RunOnceAsync();

        Assert.Empty(_sender.Sent);
        Assert.Equal(0, _booking.Calls);
        var waiting = await RecordAsync(order);
        Assert.Equal((ReminderStatuses.Pending, 0), (waiting.Status, waiting.Attempts));

        _catalog.Down = false;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Processor().RunOnceAsync();
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task WhenBookingIsDown_NothingIsSent_BecauseTheOrderCouldNotBeChecked()
    {
        var (_, order) = await OrderAsync(hoursAway: 10);
        _booking.Down = true;

        await Processor().RunOnceAsync();

        Assert.Empty(_sender.Sent);
        Assert.Equal(ReminderStatuses.Pending, (await RecordAsync(order)).Status);

        _booking.Down = false;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Processor().RunOnceAsync();
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task AStoppedPass_ReturnsUnsentRemindersToTheQueue_InsteadOfLeavingThemSending()
    {
        var (_, order) = await OrderAsync(hoursAway: 10);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Processor().RunOnceAsync(cancelled.Token);

        Assert.Empty(_sender.Sent);
        Assert.Equal(ReminderStatuses.Pending, (await RecordAsync(order)).Status);
    }

    // ---- AC5: failures are recorded and retried only when it is safe ----

    [Fact]
    public async Task ARefusedSend_IsRecorded_RetriedAfterTenMinutes_AndGivenUpAfterThreeAttempts()
    {
        var (_, order) = await OrderAsync(hoursAway: 20);
        _sender.Result = new ReminderSendResult(ReminderSendOutcome.Rejected, "The email provider refused the reminder (HTTP 503).");

        await Processor().RunOnceAsync();
        var first = await RecordAsync(order);
        Assert.Equal((ReminderStatuses.Pending, 1), (first.Status, first.Attempts));
        Assert.Contains("HTTP 503", first.LastError);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(10), first.NextAttemptAt);

        _clock.Advance(TimeSpan.FromMinutes(9));
        await Processor().RunOnceAsync();
        Assert.Single(_sender.Sent);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await Processor().RunOnceAsync();
        Assert.Equal(2, (await RecordAsync(order)).Attempts);

        _clock.Advance(TimeSpan.FromMinutes(10));
        await Processor().RunOnceAsync();
        var last = await RecordAsync(order);
        Assert.Equal((ReminderStatuses.Failed, 3), (last.Status, last.Attempts));

        _clock.Advance(TimeSpan.FromHours(2));
        await Processor().RunOnceAsync();
        Assert.Equal(3, _sender.Sent.Count);
    }

    [Fact]
    public async Task ARefusedSendThatSucceedsOnTheRetry_EndsSentOnce()
    {
        var (_, order) = await OrderAsync(hoursAway: 20);
        _sender.Result = new ReminderSendResult(ReminderSendOutcome.Rejected, "The email provider refused the reminder (HTTP 500).");
        await Processor().RunOnceAsync();

        _sender.Result = new ReminderSendResult(ReminderSendOutcome.Sent);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await Processor().RunOnceAsync();
        _clock.Advance(TimeSpan.FromMinutes(10));
        await Processor().RunOnceAsync();

        var record = await RecordAsync(order);
        Assert.Equal((ReminderStatuses.Sent, 1), (record.Status, record.Attempts));
        Assert.Equal(2, _sender.Sent.Count);
    }

    [Fact]
    public async Task ARetryIsNotMadeOnceTheShowHasStarted()
    {
        var (_, order) = await OrderAsync(hoursAway: 0.1);
        _sender.Result = new ReminderSendResult(ReminderSendOutcome.Rejected, "The email provider refused the reminder (HTTP 500).");
        await Processor().RunOnceAsync();

        _clock.Advance(TimeSpan.FromMinutes(10));
        await Processor().RunOnceAsync();

        Assert.Single(_sender.Sent);
        Assert.Equal(ReminderSkipReasons.ShowStarted, (await RecordAsync(order)).SkipReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAmbiguousSend_IsNeverRetriedAutomatically(bool senderThrows)
    {
        var (_, order) = await OrderAsync(hoursAway: 20);
        _sender.Result = new ReminderSendResult(ReminderSendOutcome.Ambiguous, "The email provider did not answer (TaskCanceledException).");
        _sender.Throws = senderThrows;

        await Processor().RunOnceAsync();
        _sender.Throws = false;
        _clock.Advance(TimeSpan.FromHours(1));
        await Processor().RunOnceAsync();
        await Processor().RunOnceAsync();

        Assert.Equal(senderThrows ? 0 : 1, _sender.Sent.Count);
        var record = await RecordAsync(order);
        Assert.Equal(ReminderStatuses.NeedsReconciliation, record.Status);
        Assert.NotNull(record.LastError);
    }

    [Fact]
    public async Task OneFailingReminderDoesNotStopTheOthersInThePass()
    {
        var (_, first) = await OrderAsync(hoursAway: 10, email: "first@example.com");
        var (_, second) = await OrderAsync(hoursAway: 10, email: "second@example.com");
        var results = new Queue<ReminderSendResult>(new[] { new ReminderSendResult(ReminderSendOutcome.Rejected, "refused (HTTP 500)"), new ReminderSendResult(ReminderSendOutcome.Simulated) });
        var sender = new SequenceSender(results);
        var processor = new ReminderProcessor(Repository(), _catalog, _booking, sender, Options.Create(new ReminderOptions()), _clock, new ReminderMetrics(_meter), NullLogger<ReminderProcessor>.Instance);

        await processor.RunOnceAsync();

        var statuses = new[] { (await RecordAsync(first)).Status, (await RecordAsync(second)).Status }.Order().ToArray();
        Assert.Equal(new[] { ReminderStatuses.Pending, ReminderStatuses.Simulated }, statuses);
    }

    private sealed class SequenceSender(Queue<ReminderSendResult> results) : IReminderEmailSender
    {
        public Task<ReminderSendResult> SendAsync(ReminderEmail email, CancellationToken cancellationToken = default) => Task.FromResult(results.Dequeue());
    }

    [Fact]
    public async Task AReminderParkedByAnotherInstanceMidPass_IsNotSentByThisOne()
    {
        var (_, first) = await OrderAsync(hoursAway: 10, email: "first@example.com");
        var (_, second) = await OrderAsync(hoursAway: 10, email: "second@example.com");
        var interfered = false;
        var sender = new HookSender(async () =>
        {
            // The first send is slow; meanwhile another instance finds the unfinished claims stale and parks them.
            if (!interfered)
            {
                interfered = true;
                _clock.Advance(TimeSpan.FromMinutes(6));
                await Repository().ClaimDueAsync(10, 5);
            }
        });
        var processor = new ReminderProcessor(Repository(), _catalog, _booking, sender, Options.Create(new ReminderOptions()), _clock, new ReminderMetrics(_meter), NullLogger<ReminderProcessor>.Instance);

        await processor.RunOnceAsync();

        Assert.Single(sender.Seen);
        var statuses = new[] { (await RecordAsync(first)).Status, (await RecordAsync(second)).Status };
        Assert.All(statuses, s => Assert.Equal(ReminderStatuses.NeedsReconciliation, s)); // the in-flight one is for people to check; the unstarted one was not sent
    }

    private sealed class HookSender(Func<Task> duringSend) : IReminderEmailSender
    {
        public List<string> Seen { get; } = new();

        public async Task<ReminderSendResult> SendAsync(ReminderEmail email, CancellationToken cancellationToken = default)
        {
            Seen.Add(email.RecipientEmail);
            await duringSend();
            return new ReminderSendResult(ReminderSendOutcome.Simulated);
        }
    }

    // ---- observability ----

    [Fact]
    public async Task EveryOutcomeIsCounted()
    {
        await OrderAsync(hoursAway: 10);
        await OrderAsync(hoursAway: 10, eligible: false);
        await OrderAsync(hoursAway: 40);

        await Processor().RunOnceAsync();

        lock (_outcomes)
        {
            Assert.Contains(("simulated", 1L), _outcomes);
            Assert.Contains(("skipped_order_not_eligible", 1L), _outcomes);
            Assert.Contains(("deferred", 1L), _outcomes);
        }
    }

    // ---- delivery stays simulated by default and never needs a provider ----

    [Fact]
    public async Task WithDefaultSettingsTheRealSenderNeverCallsAProvider()
    {
        var (_, order) = await OrderAsync(hoursAway: 10);
        var handler = new CountingHandler();
        var realSender = new ReminderEmailSender(new HttpClient(handler), Options.Create(new ReminderOptions()));
        var processor = new ReminderProcessor(Repository(), _catalog, _booking, realSender, Options.Create(new ReminderOptions()), _clock, new ReminderMetrics(_meter), NullLogger<ReminderProcessor>.Instance);

        await processor.RunOnceAsync();

        Assert.Equal(0, handler.Calls);
        Assert.Equal(ReminderStatuses.Simulated, (await RecordAsync(order)).Status);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
