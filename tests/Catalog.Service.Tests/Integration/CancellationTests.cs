using Npgsql;
using Xunit;

namespace Catalog.Service.Tests.Integration;

[Collection("Postgres")]
public class CancellationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Event_cancellation_cascades_and_records_one_durable_event_per_show()
    {
        await using var db = new NpgsqlConnection(fixture.ConnectionString);
        await db.OpenAsync();
        var eventId = Guid.CreateVersion7();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO events(id,organizer_id,name,description,category,banner_url,status) VALUES(@id,@owner,'Test','','Test','','Published');
            INSERT INTO shows(id,event_id,show_date,show_time) VALUES(@a,@id,'2026-10-10','18:00'),(@b,@id,'2026-10-11','18:00');
            UPDATE events SET status='Cancelled' WHERE id=@id;
            UPDATE events SET status='Cancelled' WHERE id=@id;
            SELECT COUNT(*) FROM show_cancellations WHERE event_id=@id;
            """, db);
        cmd.Parameters.AddWithValue("id", eventId);
        cmd.Parameters.AddWithValue("owner", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("a", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("b", Guid.CreateVersion7());
        Assert.Equal(2L, await cmd.ExecuteScalarAsync());
        await using var check = new NpgsqlCommand("SELECT COUNT(*) FROM shows WHERE event_id=@id AND status='Cancelled'", db);
        check.Parameters.AddWithValue("id", eventId);
        Assert.Equal(2L, await check.ExecuteScalarAsync());
    }
}
