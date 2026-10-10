using Npgsql;

namespace Inventory.Service.Db;

public class CancellationRepository(DbConnectionFactory factory)
{
    public async Task<bool> ReturnAsync(Guid holdId)
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT return_cancelled_hold(@id)", db);
        command.Parameters.AddWithValue("id", holdId);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    public async Task CloseAsync(Guid showId)
    {
        await using var db = (NpgsqlConnection)await factory.CreateConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT close_cancelled_show(@id)", db);
        command.Parameters.AddWithValue("id", showId);
        await command.ExecuteNonQueryAsync();
    }
}
