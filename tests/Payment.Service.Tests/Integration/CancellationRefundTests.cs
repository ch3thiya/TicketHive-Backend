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
        await new PaymentRepository(factory).CreateAsync(new PaymentTransaction
        {
            Id = Guid.CreateVersion7(),
            OrderId = id,
            CustomerSub = "customer",
            Amount = 300,
            Currency = "LKR",
            Status = PaymentStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => repo.RefundAsync(id, new RefundRequest(300, "LKR"))));
        Assert.All(results, r => Assert.Equal("Simulated", r.Status));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.RefundAsync(id, new RefundRequest(301, "LKR")));
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM refunds WHERE order_id=@id", db);
        cmd.Parameters.AddWithValue("id", id);
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Refund_requires_and_uses_verified_successful_payment_amount()
    {
        var factory = new DbConnectionFactory(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = fixture.ConnectionString }).Build());
        var repo = new RefundRepository(factory, TimeProvider.System);
        var id = Guid.CreateVersion7();
        await new PaymentRepository(factory).CreateAsync(new PaymentTransaction
        {
            Id = Guid.CreateVersion7(),
            OrderId = id,
            CustomerSub = "customer",
            Amount = 275,
            Currency = "LKR",
            Status = PaymentStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.RefundAsync(id, new RefundRequest(300, "LKR")));
        var result = await repo.RefundAsync(id, new RefundRequest(275, "LKR"));
        Assert.Equal(275, result.Amount);
    }
}
