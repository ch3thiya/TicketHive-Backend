using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Booking.Service.Clients;
using Booking.Service.Db;
using Booking.Service.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Booking.Service.Tests.Integration;

// The real Booking pipeline (routing, role authorization, controller) against real PostgreSQL.
// Only Catalog is replaced, by a fake whose answer can be set per caller.
[Collection("Postgres")]
public sealed class TicketValidationApiTests : IAsyncLifetime
{
    private readonly PostgresFixture _db;
    private TicketValidationFactory _factory = null!;

    public TicketValidationApiTests(PostgresFixture db) => _db = db;

    public Task InitializeAsync()
    {
        _factory = new TicketValidationFactory(_db.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private DbConnectionFactory Connections() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString })
        .Build());

    private async Task<(Guid ShowId, string Code, Guid OrderId)> SeedTicketAsync(OrderStatus status = OrderStatus.Confirmed)
    {
        var order = new Order
        {
            Id = Guid.CreateVersion7(), HoldId = Guid.CreateVersion7(), CustomerSub = "customer", ShowId = Guid.CreateVersion7(),
            Status = status, TotalAmount = 100, Currency = "LKR", IdempotencyKey = Guid.CreateVersion7().ToString(),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, CustomerEmail = "t@example.invalid", CustomerName = "T"
        };
        await new OrderRepository(Connections()).CreateAsync(order);
        var code = Guid.CreateVersion7().ToString();
        await new TicketRepository(Connections()).CreateTicketsAsync(new[]
        {
            new Ticket { Id = Guid.CreateVersion7(), OrderId = order.Id, ShowId = order.ShowId, CategoryId = Guid.CreateVersion7(), CustomerSub = "customer", UniqueCode = code, Price = 100, IssuedAt = DateTimeOffset.UtcNow }
        });
        return (order.ShowId, code, order.Id);
    }

    private async Task<DateTimeOffset?> UsedAtAsync(string code)
    {
        await using var connection = (NpgsqlConnection)await Connections().CreateConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT used_at FROM tickets WHERE unique_code = @c", connection);
        command.Parameters.AddWithValue("c", code);
        var value = await command.ExecuteScalarAsync();
        return value is DateTimeOffset at ? at : value is DateTime dt ? new DateTimeOffset(dt, TimeSpan.Zero) : null;
    }

    private static HttpRequestMessage Validate(string code, string? sub = null, string? role = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/booking/tickets/{code}/validate");
        if (sub is not null) request.Headers.Add("Test-Sub", sub);
        if (role is not null) request.Headers.Add("Test-Role", role);
        return request;
    }

    [Fact]
    public async Task Validate_NoToken_Returns401AndTicketStaysUnused()
    {
        var (_, code, _) = await SeedTicketAsync();

        var response = await _factory.CreateClient().SendAsync(Validate(code));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(await UsedAtAsync(code));
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("")]
    public async Task Validate_CustomerOrNoGroup_Returns403AndTicketStaysUnused(string role)
    {
        var (_, code, _) = await SeedTicketAsync();
        _factory.EntryAccess.Default = EntryAccessDecision.Allowed;

        var response = await _factory.CreateClient().SendAsync(Validate(code, "customer", role));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await UsedAtAsync(code));
        Assert.Equal(0, _factory.EntryAccess.Calls);
    }

    [Fact]
    public async Task Validate_OwningOrganizer_Returns200AndMarksTheTicketUsed()
    {
        var (showId, code, _) = await SeedTicketAsync();
        _factory.EntryAccess.Set(showId, "owner", EntryAccessDecision.Allowed);

        var response = await _factory.CreateClient().SendAsync(Validate(code, "owner", "Organizer"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(await UsedAtAsync(code));
    }

    [Fact]
    public async Task Validate_OrganizerOfAnotherShow_Returns403AndTicketStaysUnused()
    {
        var (showId, code, _) = await SeedTicketAsync();
        _factory.EntryAccess.Set(showId, "other", EntryAccessDecision.Denied);

        var response = await _factory.CreateClient().SendAsync(Validate(code, "other", "Organizer"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(await UsedAtAsync(code));
    }

    [Fact]
    public async Task Validate_CatalogDown_Returns503AndTicketStaysUnused()
    {
        var (showId, code, _) = await SeedTicketAsync();
        _factory.EntryAccess.Set(showId, "owner", EntryAccessDecision.Unavailable);

        var response = await _factory.CreateClient().SendAsync(Validate(code, "owner", "Organizer"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null(await UsedAtAsync(code));
    }

    [Fact]
    public async Task Validate_Admin_Returns200ForAnyShowWithoutAskingCatalog()
    {
        var (_, code, _) = await SeedTicketAsync();
        _factory.EntryAccess.Default = EntryAccessDecision.Denied;

        var response = await _factory.CreateClient().SendAsync(Validate(code, "admin-1", "Admin"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, _factory.EntryAccess.Calls);
        Assert.NotNull(await UsedAtAsync(code));
    }

    [Fact]
    public async Task Validate_SecondScan_Returns409WithoutNamingTheFirstScanner()
    {
        var (showId, code, _) = await SeedTicketAsync();
        _factory.EntryAccess.Set(showId, "gate-a-organizer", EntryAccessDecision.Allowed);
        var client = _factory.CreateClient();
        await client.SendAsync(Validate(code, "gate-a-organizer", "Organizer"));

        var second = await client.SendAsync(Validate(code, "gate-a-organizer", "Organizer"));
        var text = await second.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.DoesNotContain("gate-a-organizer", text);
        Assert.Contains("already used", text);
    }

    [Fact]
    public async Task Validate_RefusedCallerLearnsNothingAboutAUsedTicket()
    {
        var (showId, code, _) = await SeedTicketAsync();
        _factory.EntryAccess.Set(showId, "owner", EntryAccessDecision.Allowed);
        _factory.EntryAccess.Set(showId, "other", EntryAccessDecision.Denied);
        await _factory.CreateClient().SendAsync(Validate(code, "owner", "Organizer"));

        var response = await _factory.CreateClient().SendAsync(Validate(code, "other", "Organizer"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Validate_AfterOrderCancellation_OwnerGets409AndTicketStaysVoided()
    {
        var (showId, code, orderId) = await SeedTicketAsync();
        _factory.EntryAccess.Set(showId, "owner", EntryAccessDecision.Allowed);
        var cancelled = await new CancellationRepository(Connections(), TimeProvider.System).CancelAsync(orderId, "customer", DateTimeOffset.MaxValue, false);
        Assert.Null(cancelled);

        var response = await _factory.CreateClient().SendAsync(Validate(code, "owner", "Organizer"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(await UsedAtAsync(code));
        Assert.NotNull((await new TicketRepository(Connections()).GetByCodeAsync(code))!.VoidedAt);
    }

    [Fact]
    public async Task Validate_ManyParallelScansByTheOwner_ExactlyOneSucceeds()
    {
        var (showId, code, _) = await SeedTicketAsync();
        _factory.EntryAccess.Set(showId, "owner", EntryAccessDecision.Allowed);
        var client = _factory.CreateClient();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scans = Enumerable.Range(0, 20).Select(async _ =>
        {
            await gate.Task;
            return (await client.SendAsync(Validate(code, "owner", "Organizer"))).StatusCode;
        }).ToArray();

        gate.SetResult();
        var results = await Task.WhenAll(scans);

        Assert.Equal(1, results.Count(r => r == HttpStatusCode.OK));
        Assert.Equal(19, results.Count(r => r == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Validate_DeniedCallersRacingTheOwner_NeverSetUsedAtThemselves()
    {
        var (showId, code, _) = await SeedTicketAsync();
        _factory.EntryAccess.Set(showId, "other", EntryAccessDecision.Denied);
        var client = _factory.CreateClient();

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ => (await client.SendAsync(Validate(code, "other", "Organizer"))).StatusCode));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Forbidden, r));
        Assert.Null(await UsedAtAsync(code));
    }
}

internal sealed class FakeEntryAccessClient : IEntryAccessClient
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, string), EntryAccessDecision> _answers = new();
    private int _calls;

    public EntryAccessDecision Default { get; set; } = EntryAccessDecision.Denied;
    public int Calls => Volatile.Read(ref _calls);

    public void Set(Guid showId, string sub, EntryAccessDecision decision) => _answers[(showId, sub)] = decision;

    public Task<EntryAccessDecision> CheckAsync(Guid showId, string sub, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(_answers.TryGetValue((showId, sub), out var decision) ? decision : Default);
    }
}

internal sealed class TicketValidationFactory(string connectionString) : WebApplicationFactory<Program>
{
    public FakeEntryAccessClient EntryAccess { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = connectionString }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
            services.RemoveAll<IEntryAccessClient>();
            services.AddSingleton<IEntryAccessClient>(EntryAccess);
            services.AddAuthentication("ValidationTest").AddScheme<AuthenticationSchemeOptions, ValidationTestAuthHandler>("ValidationTest", _ => { });
            services.PostConfigure<AuthenticationOptions>(o => { o.DefaultAuthenticateScheme = "ValidationTest"; o.DefaultChallengeScheme = "ValidationTest"; o.DefaultScheme = "ValidationTest"; });
        });
    }
}

internal sealed class ValidationTestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Test-Sub", out var sub)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, sub.ToString()) };
        if (Request.Headers.TryGetValue("Test-Role", out var role) && !string.IsNullOrEmpty(role)) claims.Add(new Claim(ClaimTypes.Role, role.ToString()));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}