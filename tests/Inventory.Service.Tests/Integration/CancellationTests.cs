using BuildingBlocks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Inventory.Service.Tests.Integration;

[Collection("Postgres")]
public class CancellationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Repeated_show_closure_releases_holds_and_blocks_new_admission()
    {
        var show = Guid.CreateVersion7();
        var category = Guid.CreateVersion7();
        var hold = Guid.CreateVersion7();
        await using var db = new NpgsqlConnection(fixture.ConnectionString);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO stock VALUES(@s,@c,10,8,100,'LKR','GA',0);
            INSERT INTO customer_quotas VALUES(@s,'customer',2);
            INSERT INTO holds VALUES(@h,@s,'customer','PaymentPending',CURRENT_TIMESTAMP+interval '10 minutes',@h::text,CURRENT_TIMESTAMP);
            INSERT INTO hold_items VALUES(@h,@c,2,100);
            SELECT close_cancelled_show(@s);
            SELECT close_cancelled_show(@s);
            """, db);
        cmd.Parameters.AddWithValue("s", show);
        cmd.Parameters.AddWithValue("c", category);
        cmd.Parameters.AddWithValue("h", hold);
        await cmd.ExecuteNonQueryAsync();
        var factory = new Inventory.Service.Db.DbConnectionFactory(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = fixture.ConnectionString }).Build());
        var repo = new Inventory.Service.Db.HoldRepository(factory, new Inventory.Service.Db.GeneralAdmissionAllocationStrategy());
        var result = await repo.CreateAsync(new Inventory.Service.Models.Hold
        {
            Id = Guid.CreateVersion7(),
            ShowId = show,
            CustomerSub = "new",
            Status = Inventory.Service.Models.HoldStatus.Active,
            IdempotencyKey = Guid.CreateVersion7().ToString(),
            CreatedAt = TimeProvider.System.GetUtcNow(),
            ExpiresAt = TimeProvider.System.GetUtcNow().AddMinutes(10),
            Items = new List<Inventory.Service.Models.HoldItem> { new() { CategoryId = category, Quantity = 1 } }
        }, 6);
        Assert.Equal(Inventory.Service.Db.HoldCreationOutcome.StockUnavailable, result.Outcome);
        await using var verify = new NpgsqlCommand("SELECT available FROM stock WHERE show_id=@s", db);
        verify.Parameters.AddWithValue("s", show);
        Assert.Equal(10, await verify.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Concurrent_returns_restore_sold_stock_and_quota_once()
    {
        var show = Guid.CreateVersion7();
        var category = Guid.CreateVersion7();
        var hold = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var seed = new NpgsqlCommand("""
            INSERT INTO stock VALUES (@s,@c,10,8,100,'LKR','GA',2);
            INSERT INTO customer_quotas VALUES (@s,'customer',2);
            INSERT INTO holds VALUES (@h,@s,'customer','Converted',CURRENT_TIMESTAMP,'cancel-test-' || @h,CURRENT_TIMESTAMP);
            INSERT INTO hold_items VALUES (@h,@c,2,100);
            """, connection);
        seed.Parameters.AddWithValue("s", show);
        seed.Parameters.AddWithValue("c", category);
        seed.Parameters.AddWithValue("h", hold);
        await seed.ExecuteNonQueryAsync();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = new NpgsqlConnection(fixture.ConnectionString);
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT return_cancelled_hold(@h)", db);
            cmd.Parameters.AddWithValue("h", hold);
            await cmd.ExecuteNonQueryAsync();
        }));
        await using var verify = new NpgsqlCommand("SELECT available, sold, (SELECT quantity FROM customer_quotas WHERE show_id=@s) FROM stock WHERE show_id=@s", connection);
        verify.Parameters.AddWithValue("s", show);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(10, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(0, reader.GetInt32(2));
    }
}
