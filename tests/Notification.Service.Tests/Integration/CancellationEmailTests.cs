using Microsoft.Extensions.Configuration;
using Notification.Service.Db;
using Notification.Service.Models;
using Xunit;

namespace Notification.Service.Tests.Integration;

[Collection("Postgres")]
public class CancellationEmailTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Duplicate_show_customer_requests_are_claimed_once_and_never_resent()
    {
        var factory = new DbConnectionFactory(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = fixture.ConnectionString }).Build());
        var repo = new CancellationEmailRepository(factory, TimeProvider.System);
        var email = new CancellationEmail(Guid.CreateVersion7().ToString(), "test@example.invalid", "Test", Guid.CreateVersion7(), new[] { Guid.CreateVersion7() }, 200, "LKR", true, "Show");
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => repo.EnqueueAsync(email)));
        var claimed = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => repo.ClaimAsync()));
        Assert.Single(claimed, e => e is not null);
        await repo.CompleteAsync(email.Key, "Simulated");
        Assert.Equal("Simulated", await repo.EnqueueAsync(email));
        Assert.Null(await repo.ClaimAsync());
    }

    [Fact]
    public async Task Ambiguous_delivery_is_terminal_and_exposed_for_reconciliation()
    {
        var factory = new DbConnectionFactory(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = fixture.ConnectionString }).Build());
        var repo = new CancellationEmailRepository(factory, TimeProvider.System);
        var email = new CancellationEmail(Guid.CreateVersion7().ToString(), "test@example.invalid", "Test", Guid.CreateVersion7(), new[] { Guid.CreateVersion7() }, 200, "LKR", false, "Show");
        await repo.EnqueueAsync(email);
        Assert.NotNull(await repo.ClaimAsync());
        await repo.CompleteAsync(email.Key, "NeedsReconciliation");

        Assert.Equal("NeedsReconciliation", await repo.GetStatusAsync(email.Key));
        Assert.Null(await repo.ClaimAsync());
        Assert.Equal("NeedsReconciliation", await repo.EnqueueAsync(email));
    }
}
