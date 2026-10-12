using System.Net;
using System.Text.Json;
using BuildingBlocks;
using Microsoft.Extensions.Options;
using Notification.Service.Models;
using Notification.Service.Services;
using Xunit;

namespace Notification.Service.Tests;

public class ReminderRulesTests
{
    [Theory]
    [InlineData("buyer@example.com", true)]
    [InlineData("first.last+tag@mail.example.co.lk", true)]
    [InlineData("customer@tickethive.lk", false)]
    [InlineData("Customer@TicketHive.LK", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("no-at-sign", false)]
    [InlineData("a@b", false)]
    [InlineData("two@@example.com", false)]
    [InlineData("two@at@example.com", false)]
    [InlineData("has space@example.com", false)]
    [InlineData("@example.com", false)]
    [InlineData("user@example.", false)]
    [InlineData("user-sub-without-an-email", false)]
    public void OnlyARealAddressIsDeliverable(string? email, bool expected) =>
        Assert.Equal(expected, ReminderRules.IsDeliverableRecipient(email));

    private static readonly DateTimeOffset Now = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    [Theory]
    [InlineData(25 * 3600, ShowReminderWindow.NotYet)]
    [InlineData(24 * 3600 + 1, ShowReminderWindow.NotYet)]
    [InlineData(24 * 3600, ShowReminderWindow.Due)]
    [InlineData(24 * 3600 - 1, ShowReminderWindow.Due)]
    [InlineData(3 * 3600, ShowReminderWindow.Due)]
    [InlineData(1, ShowReminderWindow.Due)]
    [InlineData(0, ShowReminderWindow.Started)]
    [InlineData(-3600, ShowReminderWindow.Started)]
    public void AShowIsDueWhenItStartsWithinTheWindowAndHasNotStarted(int secondsUntilStart, ShowReminderWindow expected) =>
        Assert.Equal(expected, ReminderRules.Evaluate(Now.AddSeconds(secondsUntilStart), Now, Window));

    [Fact]
    public void ADistantShowIsLookedAtWhenItsWindowOpens_ButNeverLaterThanTheCap()
    {
        var cap = TimeSpan.FromHours(6);

        Assert.Equal(Now.AddHours(2), ReminderRules.NextLook(Now.AddHours(26), Now, Window, cap));
        Assert.Equal(Now.AddHours(6), ReminderRules.NextLook(Now.AddHours(200), Now, Window, cap));
    }

    [Fact]
    public void ALookIsNeverSoonerThanAMinute() =>
        Assert.Equal(Now.AddMinutes(1), ReminderRules.NextLook(Now.AddHours(24).AddSeconds(5), Now, Window, TimeSpan.FromHours(6)));
}

public class ReminderEmailComposerTests
{
    private static ReminderRow Row(string name = "Bea Buyer") => new(Guid.NewGuid(), Guid.NewGuid(), "bea@example.com", name, 2, 0);

    private static ReminderShow Show(string? venue = "Nelum Pokuna") =>
        new(Guid.NewGuid(), new DateTimeOffset(2026, 12, 1, 13, 30, 0, TimeSpan.Zero), "Arijit Singh Live", venue, false);

    [Fact]
    public void NamesTheEventTheDateAndTimeInSriLankaTheVenueAndTheTicketCount()
    {
        var email = ReminderEmailComposer.Compose(Row(), Show(), 3);

        Assert.Contains("Arijit Singh Live", email.Message);
        Assert.Contains("Tuesday, 1 December 2026 at 7:00 PM (Sri Lanka time)", email.Message);
        Assert.Contains("Venue: Nelum Pokuna", email.Message);
        Assert.Contains("You have 3 tickets", email.Message);
        Assert.Contains("Arijit Singh Live", email.Subject);
        Assert.Equal(("bea@example.com", 3), (email.RecipientEmail, email.TicketCount));
    }

    [Fact]
    public void ASingleTicketIsSingular() =>
        Assert.Contains("You have 1 ticket.", ReminderEmailComposer.Compose(Row(), Show(), 1).Message);

    [Fact]
    public void AMissingVenueIsSaidPlainlyNotLeftBlank() =>
        Assert.Contains("Venue: Venue to be announced", ReminderEmailComposer.Compose(Row(), Show(null), 2).Message);

    [Fact]
    public void ABlankNameStillReadsNaturally() =>
        Assert.StartsWith("Hi there,", ReminderEmailComposer.Compose(Row(" "), Show(), 2).Message);

    [Fact]
    public void TheTimeFollowsTheVenueClock_NotTheServers()
    {
        var late = new ReminderShow(Guid.NewGuid(), new DateTimeOffset(2026, 12, 1, 19, 0, 0, TimeSpan.Zero), "Late Show", "Hall", false);

        // 19:00 UTC is 00:30 the next day in Sri Lanka.
        Assert.Contains("Wednesday, 2 December 2026 at 12:30 AM", ReminderEmailComposer.Compose(Row(), late, 1).Message);
    }
}

public class ReminderEmailSenderTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private static readonly ReminderEmail Email = new("bea@example.com", "Bea", "Reminder: Gig", "Hi Bea", "Gig", "Tuesday", "Hall", 2);

    private static ReminderEmailSender Sender(StubHandler handler, ReminderOptions options) =>
        new(new HttpClient(handler), Options.Create(options));

    private static ReminderOptions Live() => new() { Simulate = false, ServiceId = "svc", TemplateId = "tpl", PublicKey = "pub", PrivateKey = "priv" };

    [Fact]
    public void SimulationIsTheDefault() => Assert.True(new ReminderOptions().Simulate);

    [Fact]
    public async Task WhenSimulated_NothingIsSentToAnyProvider()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var result = await Sender(handler, new ReminderOptions()).SendAsync(Email);

        Assert.Equal(ReminderSendOutcome.Simulated, result.Outcome);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task LiveDeliveryWithoutProviderSettings_IsRefusedAndSendsNothing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var result = await Sender(handler, new ReminderOptions { Simulate = false }).SendAsync(Email);

        Assert.Equal(ReminderSendOutcome.Rejected, result.Outcome);
        Assert.Contains("not configured", result.Error);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ALiveSendPassesTheReminderDetailsToTheProvider()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var result = await Sender(handler, Live()).SendAsync(Email);

        Assert.Equal(ReminderSendOutcome.Sent, result.Outcome);
        using var body = JsonDocument.Parse(handler.Body!);
        var template = body.RootElement.GetProperty("template_params");
        Assert.Equal("bea@example.com", template.GetProperty("to_email").GetString());
        Assert.Equal("Gig", template.GetProperty("event_name").GetString());
        Assert.Equal("Hall", template.GetProperty("venue").GetString());
        Assert.Equal(2, template.GetProperty("ticket_count").GetInt32());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AProviderRefusalIsDefinite_AndItsReplyBodyIsNeverRecorded(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("secret-token-abc") });

        var result = await Sender(handler, Live()).SendAsync(Email);

        Assert.Equal(ReminderSendOutcome.Rejected, result.Outcome);
        Assert.Contains($"HTTP {(int)status}", result.Error);
        Assert.DoesNotContain("secret-token-abc", result.Error);
        Assert.DoesNotContain("priv", result.Error);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    public async Task NoAnswerAtAllIsAmbiguous(Type exception)
    {
        var handler = new StubHandler(_ => throw (Exception)Activator.CreateInstance(exception)!);

        var result = await Sender(handler, Live()).SendAsync(Email);

        Assert.Equal(ReminderSendOutcome.Ambiguous, result.Outcome);
    }
}

public class ReminderWorkerConfigurationTests
{
    [Theory]
    [InlineData("client", "secret", true)]
    [InlineData("YOUR_ASGARDEO_M2M_CLIENT_ID", "secret", false)]
    [InlineData("client", "YOUR_ASGARDEO_M2M_CLIENT_SECRET", false)]
    [InlineData("", "secret", false)]
    [InlineData("client", "", false)]
    public void TheJobWaitsUntilRealServiceCredentialsAreSet(string clientId, string secret, bool configured) =>
        Assert.Equal(configured, ReminderWorker.DependenciesConfigured(new InternalServiceTokenClientOptions { ClientId = clientId, ClientSecret = secret }));

    [Fact]
    public void TheWindowIsTwentyFourHoursAndRetriesAreBounded()
    {
        var options = new ReminderOptions();

        Assert.Equal((24, 3, 10), (options.WindowHours, options.MaxAttempts, options.RetryMinutes));
    }
}
