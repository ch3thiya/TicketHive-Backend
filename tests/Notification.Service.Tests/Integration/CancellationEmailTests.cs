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
}
