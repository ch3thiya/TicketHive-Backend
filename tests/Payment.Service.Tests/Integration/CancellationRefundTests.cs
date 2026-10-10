using Microsoft.Extensions.Configuration;
using Npgsql;
using Payment.Service.Db;
using Payment.Service.Models;
using Xunit;

namespace Payment.Service.Tests.Integration;

[Collection("Postgres")]
public class CancellationRefundTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Concurrent_refunds_record_one_simulation_and_reject_payload_mismatch()
    {
        var factory = new DbConnectionFactory(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = fixture.ConnectionString }).Build());
        var repo = new RefundRepository(factory, TimeProvider.System);
        var id = Guid.CreateVersion7();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => repo.RefundAsync(id, new RefundRequest(300, "LKR"))));
        Assert.All(results, r => Assert.Equal("Simulated", r.Status));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.RefundAsync(id, new RefundRequest(301, "LKR")));
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM refunds WHERE order_id=@id", db);
        cmd.Parameters.AddWithValue("id", id);
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
    }
}
