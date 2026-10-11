using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Notification.Service.Db;
using Notification.Service.Models;
using Notification.Service.Services;
using Xunit;

namespace Notification.Service.Tests.Integration;

// A tickets-issued event becomes exactly one reminder row; nothing else is created.
[Collection("Postgres")]
public sealed class ReminderSeederTests
{
    private readonly PostgresFixture _db;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));

    public ReminderSeederTests(PostgresFixture db) => _db = db;

    private EventReminderRepository Repository() => new(new DbConnectionFactory(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString })
        .Build()), _clock);

    private ReminderSeeder Seeder() => new(Repository(), NullLogger<ReminderSeeder>.Instance);

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task ATicketsIssuedEvent_RecordsOnePendingReminder()
    {
        var order = Guid.CreateVersion7();
        var show = Guid.CreateVersion7();

        var created = await Seeder().SeedAsync(Payload($$"""{"ShowId":"{{show}}","TicketCount":3}"""), order, "buyer@example.com", "Bea");

        Assert.True(created);
        var record = await Repository().GetAsync(order);
        Assert.Equal((ReminderStatuses.Pending, 0), (record!.Status, record.Attempts));
        var claimed = (await Repository().ClaimDueAsync(1000, 5)).Single(r => r.OrderId == order);
        Assert.Equal((show, 3, "buyer@example.com"), (claimed.ShowId, claimed.TicketCount, claimed.RecipientEmail));
    }

    [Fact]
    public async Task ARedeliveredEvent_DoesNotCreateASecondReminder()
    {
        var order = Guid.CreateVersion7();
        var payload = Payload($$"""{"ShowId":"{{Guid.CreateVersion7()}}","TicketCount":1}""");

        var results = new[]
        {
            await Seeder().SeedAsync(payload, order, "buyer@example.com", "Bea"),
            await Seeder().SeedAsync(payload, order, "buyer@example.com", "Bea"),
        };

        Assert.Equal(new[] { true, false }, results);
    }

    [Theory]
    [InlineData("customer@tickethive.lk")]
    [InlineData("not-an-email")]
    [InlineData("")]
    public async Task APlaceholderOrInvalidAddress_IsRecordedAsMissingRecipient(string email)
    {
        var order = Guid.CreateVersion7();

        await Seeder().SeedAsync(Payload($$"""{"ShowId":"{{Guid.CreateVersion7()}}","TicketCount":1}"""), order, email, "Bea");

        Assert.Equal(ReminderStatuses.MissingRecipient, (await Repository().GetAsync(order))!.Status);
    }

    [Theory]
    [InlineData("""{"TicketCount":2}""")]
    [InlineData("""{"ShowId":"","TicketCount":2}""")]
    [InlineData("""{"ShowId":"not-a-guid","TicketCount":2}""")]
    [InlineData("""{"ShowId":"00000000-0000-0000-0000-000000000000","TicketCount":2}""")]
    [InlineData("""{"ShowId":12,"TicketCount":2}""")]
    public async Task AnEventWithoutAUsableShowId_CreatesNothing(string json)
    {
        var order = Guid.CreateVersion7();

        var created = await Seeder().SeedAsync(Payload(json), order, "buyer@example.com", "Bea");

        Assert.False(created);
        Assert.Null(await Repository().GetAsync(order));
    }

    [Fact]
    public async Task WithoutATicketCount_TheNumberOfTicketCodesIsUsed()
    {
        var order = Guid.CreateVersion7();

        await Seeder().SeedAsync(Payload($$"""{"ShowId":"{{Guid.CreateVersion7()}}","TicketCodes":["a","b","c","d"]}"""), order, "buyer@example.com", "Bea");

        var claimed = (await Repository().ClaimDueAsync(1000, 5)).Single(r => r.OrderId == order);
        Assert.Equal(4, claimed.TicketCount);
    }

    [Fact]
    public async Task WithNoCountAtAll_AtLeastOneTicketIsRecorded()
    {
        var order = Guid.CreateVersion7();

        await Seeder().SeedAsync(Payload($$"""{"ShowId":"{{Guid.CreateVersion7()}}"}"""), order, "buyer@example.com", "Bea");

        var claimed = (await Repository().ClaimDueAsync(1000, 5)).Single(r => r.OrderId == order);
        Assert.Equal(1, claimed.TicketCount);
    }
}
