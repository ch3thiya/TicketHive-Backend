using Booking.Service.Db;
using Booking.Service.Models;
using Booking.Service.Clients;
using Booking.Service.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Xunit;

namespace Booking.Service.Tests.Integration;

[Collection("Postgres")]
public class CancellationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Recovery_after_lost_refund_response_returns_stock_refunds_and_notifies_once()
    {
        var (order, _) = await SeedAsync();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(TimeProvider.System.GetUtcNow());
        var repository = new CancellationRepository(Factory, clock);
        await repository.CancelAsync(order.Id, "owner", DateTimeOffset.MaxValue, false);
        await repository.CancelAsync(order.Id, "owner", DateTimeOffset.MaxValue, false);
        var remote = new IdempotentRemote(order.Id);
        var services = new ServiceCollection();
        services.AddSingleton(Factory);
        services.AddSingleton<TimeProvider>(clock);
        services.AddScoped<CancellationRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddSingleton<IHttpClientFactory>(remote);
        services.AddScoped<CancellationClient>();
        using var provider = services.BuildServiceProvider();
        using var meter = new Meter("Test");
        using var worker = new CancellationWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CancellationWorker>.Instance, meter);
        await worker.ProcessBatchAsync();
        Assert.Equal("Pending", (await repository.GetAsync(order.Id))!.RefundStatus);
        await using var db = (NpgsqlConnection)await Factory.CreateConnectionAsync();
        await using var retry = new NpgsqlCommand("UPDATE order_cancellations SET retry_at=@now WHERE order_id=@id", db);
        retry.Parameters.AddWithValue("id", order.Id);
        retry.Parameters.AddWithValue("now", clock.GetUtcNow());
        await retry.ExecuteNonQueryAsync();
        await worker.ProcessBatchAsync();
        await worker.ProcessBatchAsync();
        Assert.Equal("Simulated", (await repository.GetAsync(order.Id))!.RefundStatus);
        Assert.Equal("Simulated", (await repository.GetAsync(order.Id))!.NotificationStatus);
        Assert.Equal(1, remote.Refunds.Count(id => id == order.Id.ToString()));
        Assert.Equal(1, remote.Returns.Count(id => id == order.HoldId.ToString()));
        Assert.Equal(1, remote.Emails.Count(key => key == $"order:{order.Id}"));
        Assert.Equal(2, remote.TargetRefundRequests);
    }

    private sealed class IdempotentRemote(Guid target) : HttpMessageHandler, IHttpClientFactory
    {
        public HashSet<string> Refunds { get; } = new();
        public HashSet<string> Returns { get; } = new();
        public HashSet<string> Emails { get; } = new();
        public int TargetRefundRequests { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("http://fake.invalid/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var id = path.Split('/').Last();
            if (path.Contains("/holds/")) Returns.Add(id);
            if (path.Contains("/refunds/"))
            {
                Refunds.Add(id);
                if (id == target.ToString() && ++TargetRefundRequests == 1) throw new HttpRequestException("Injected loss after refund committed.");
            }
            if (path.Contains("/notification/"))
            {
                var email = await request.Content!.ReadFromJsonAsync<CancellationEmail>(cancellationToken);
                Emails.Add(email!.Key);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { Status = "Simulated" }) };
        }
    }

    [Fact]
    public async Task Late_payment_after_show_cancellation_schedules_refund_without_reconfirmation()
    {
        var (order, _) = await SeedAsync(status: OrderStatus.PaymentPending);
        var repo = new CancellationRepository(Factory, TimeProvider.System);
        await repo.CancelShowAsync(order.ShowId);
        Assert.Equal("NotRequired", (await repo.GetAsync(order.Id))!.RefundStatus);
        Assert.False(await new OrderRepository(Factory).UpdateStatusAsync(order.Id, OrderStatus.Confirmed, TimeProvider.System.GetUtcNow()));
        Assert.Equal(OrderStatus.Cancelled, (await new OrderRepository(Factory).GetByIdAsync(order.Id))!.Status);
        Assert.Equal("Pending", (await repo.GetAsync(order.Id))!.RefundStatus);
        Assert.False(await new OrderRepository(Factory).UpdateStatusAsync(order.Id, OrderStatus.Confirmed, TimeProvider.System.GetUtcNow()));
        Assert.Equal("Pending", (await repo.GetAsync(order.Id))!.RefundStatus);
    }

    private DbConnectionFactory Factory => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["ConnectionStrings:DefaultConnection"] = fixture.ConnectionString }).Build());

    private async Task<(Order Order, string Code)> SeedAsync(bool used = false, OrderStatus status = OrderStatus.Confirmed, Guid? show = null)
    {
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            HoldId = Guid.CreateVersion7(),
            CustomerSub = "owner",
            ShowId = show ?? Guid.CreateVersion7(),
            Status = status,
            TotalAmount = 200,
            Currency = "LKR",
            IdempotencyKey = Guid.CreateVersion7().ToString(),
            CreatedAt = TimeProvider.System.GetUtcNow(),
            UpdatedAt = TimeProvider.System.GetUtcNow(),
            CustomerEmail = "test@example.invalid",
            CustomerName = "Test"
        };
        await new OrderRepository(Factory).CreateAsync(order);
        var code = Guid.CreateVersion7().ToString();
        if (status == OrderStatus.Confirmed)
            await new TicketRepository(Factory).CreateTicketsAsync(new[] { new Ticket { Id = Guid.CreateVersion7(), OrderId = order.Id, ShowId = order.ShowId, CategoryId = Guid.CreateVersion7(), CustomerSub = "owner", UniqueCode = code, Price = 200, IssuedAt = TimeProvider.System.GetUtcNow(), UsedAt = used ? TimeProvider.System.GetUtcNow() : null } });
        return (order, code);
    }

    [Fact]
    public async Task Repeated_cancellation_voids_once_and_cannot_be_reconfirmed_or_validated()
    {
        var (order, code) = await SeedAsync();
        var repo = new CancellationRepository(Factory, TimeProvider.System);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => repo.CancelAsync(order.Id, "owner", DateTimeOffset.MaxValue, false)));
        Assert.All(results, Assert.Null);
        Assert.Equal(OrderStatus.Cancelled, (await new OrderRepository(Factory).GetByIdAsync(order.Id))!.Status);
        Assert.False(await new OrderRepository(Factory).UpdateStatusAsync(order.Id, OrderStatus.Confirmed, TimeProvider.System.GetUtcNow()));
        Assert.False(await new TicketRepository(Factory).ValidateAndUseTicketAsync(code, "staff", TimeProvider.System.GetUtcNow()));
        Assert.NotNull((await new TicketRepository(Factory).GetByCodeAsync(code))!.VoidedAt);
        Assert.Equal("Pending", (await repo.GetAsync(order.Id))!.RefundStatus);
        await using var db = (NpgsqlConnection)await Factory.CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM order_cancellations WHERE order_id=@id", db);
        cmd.Parameters.AddWithValue("id", order.Id);
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("other", false, false, "NotFound")]
    [InlineData("owner", true, false, "TicketUsed")]
    [InlineData("owner", false, true, "ShowStarted")]
    public async Task Refused_customer_cancellation_changes_nothing(string owner, bool used, bool started, string expected)
    {
        var (order, code) = await SeedAsync(used);
        var repo = new CancellationRepository(Factory, TimeProvider.System);
        Assert.Equal(expected, await repo.CancelAsync(order.Id, owner, started ? DateTimeOffset.MinValue : DateTimeOffset.MaxValue, false));
        Assert.Null(await repo.GetAsync(order.Id));
        Assert.Equal(OrderStatus.Confirmed, (await new OrderRepository(Factory).GetByIdAsync(order.Id))!.Status);
        Assert.Null((await new TicketRepository(Factory).GetByCodeAsync(code))!.VoidedAt);
    }

    [Fact]
    public async Task Validation_racing_cancellation_has_only_one_winner()
    {
        for (var i = 0; i < 12; i++)
        {
            var (order, code) = await SeedAsync();
            var cancellation = new CancellationRepository(Factory, TimeProvider.System).CancelAsync(order.Id, "owner", DateTimeOffset.MaxValue, false);
            var validation = new TicketRepository(Factory).ValidateAndUseTicketAsync(code, "staff", TimeProvider.System.GetUtcNow());
            await Task.WhenAll(cancellation, validation);
            Assert.Equal(await validation ? "TicketUsed" : null, await cancellation);
        }
    }

    [Fact]
    public async Task Show_cancellation_includes_used_and_unpaid_orders_and_groups_email()
    {
        var (paid, _) = await SeedAsync(true);
        var (unpaid, _) = await SeedAsync(status: OrderStatus.PaymentPending, show: paid.ShowId);
        var repo = new CancellationRepository(Factory, TimeProvider.System);
        await repo.CancelShowAsync(paid.ShowId);
        await repo.CancelShowAsync(paid.ShowId);
        Assert.Equal("Pending", (await repo.GetAsync(paid.Id))!.RefundStatus);
        Assert.Equal("NotRequired", (await repo.GetAsync(unpaid.Id))!.RefundStatus);
        Assert.DoesNotContain(await repo.ReadyEmailsAsync(), e => e.ShowId == paid.ShowId);
        await using var db = (NpgsqlConnection)await Factory.CreateConnectionAsync();
        await using var finish = new NpgsqlCommand("UPDATE order_cancellations SET inventory_returned=true,refund_status=CASE WHEN refund_status='Pending' THEN 'Simulated' ELSE refund_status END WHERE order_id=ANY(@ids)", db);
        finish.Parameters.AddWithValue("ids", new[] { paid.Id, unpaid.Id });
        await finish.ExecuteNonQueryAsync();
        var email = Assert.Single((await repo.ReadyEmailsAsync()), e => e.ShowId == paid.ShowId);
        Assert.Equal(2, email.OrderIds.Length);
        Assert.Equal(200, email.Amount);
        await Assert.ThrowsAsync<PostgresException>(() => SeedAsync(status: OrderStatus.PaymentPending, show: paid.ShowId));
    }

    [Fact]
    public async Task No_new_order_can_commit_after_show_cancellation_is_acknowledged()
    {
        var show = Guid.CreateVersion7();
        var cancellations = new CancellationRepository(Factory, TimeProvider.System);
        await cancellations.CancelShowAsync(show);

        var order = new Order
        {
            Id = Guid.CreateVersion7(), HoldId = Guid.CreateVersion7(), CustomerSub = "customer", ShowId = show,
            Status = OrderStatus.PaymentPending, TotalAmount = 100, Currency = "LKR", IdempotencyKey = Guid.CreateVersion7().ToString(),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        await Assert.ThrowsAsync<PostgresException>(() => new OrderRepository(Factory).CreateAsync(order));
    }

    [Fact]
    public async Task Order_creation_racing_show_cancellation_is_rejected_or_cancelled()
    {
        for (var i = 0; i < 8; i++)
        {
            var show = Guid.CreateVersion7();
            var order = new Order
            {
                Id = Guid.CreateVersion7(), HoldId = Guid.CreateVersion7(), CustomerSub = "customer", ShowId = show,
                Status = OrderStatus.PaymentPending, TotalAmount = 100, Currency = "LKR", IdempotencyKey = Guid.CreateVersion7().ToString(),
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            var create = new OrderRepository(Factory).CreateAsync(order);
            var cancel = new CancellationRepository(Factory, TimeProvider.System).CancelShowAsync(show);
            try { await Task.WhenAll(create, cancel); }
            catch (PostgresException) { await cancel; }

            var stored = await new OrderRepository(Factory).GetByIdAsync(order.Id);
            Assert.True(stored is null || stored.Status == OrderStatus.Cancelled);
        }
    }

    [Fact]
    public async Task Notification_reconciliation_is_terminal_and_not_polled_again()
    {
        var (order, _) = await SeedAsync();
        var repo = new CancellationRepository(Factory, TimeProvider.System);
        await repo.CancelAsync(order.Id, "owner", DateTimeOffset.MaxValue, false);
        await using var db = (NpgsqlConnection)await Factory.CreateConnectionAsync();
        await using var finish = new NpgsqlCommand("UPDATE order_cancellations SET inventory_returned=true,refund_status='Simulated',notification_status='NeedsReconciliation' WHERE order_id=@id", db);
        finish.Parameters.AddWithValue("id", order.Id);
        await finish.ExecuteNonQueryAsync();

        Assert.DoesNotContain(await repo.ReadyEmailsAsync(), email => email.OrderIds.Contains(order.Id));
    }
}
