using System;
using System.Threading.Tasks;
using Npgsql;

namespace WaitingRoom.Service.Tests.Integration;

internal static class TestData
{
    public static async Task SeedQueueAsync(
        string connectionString,
        Guid showId,
        DateTimeOffset onSaleAt,
        int admitBatch = 50,
        int admitIntervalSeconds = 30,
        string status = "PreQueue")
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into queues (show_id, on_sale_at, prequeue_opens_at, admit_batch, admit_interval_seconds, status)
            values (@showId, @onSaleAt, @prequeueOpensAt, @admitBatch, @admitIntervalSeconds, @status)
            """;
        command.Parameters.AddWithValue("showId", showId);
        command.Parameters.AddWithValue("onSaleAt", onSaleAt);
        command.Parameters.AddWithValue("prequeueOpensAt", onSaleAt.AddMinutes(-30));
        command.Parameters.AddWithValue("admitBatch", admitBatch);
        command.Parameters.AddWithValue("admitIntervalSeconds", admitIntervalSeconds);
        command.Parameters.AddWithValue("status", status);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task SetNextNumberAsync(string connectionString, Guid showId, long nextNumber)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "update queues set next_number = @nextNumber where show_id = @showId";
        command.Parameters.AddWithValue("nextNumber", nextNumber);
        command.Parameters.AddWithValue("showId", showId);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<long> GetServingNumberAsync(string connectionString, Guid showId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select serving_number from queues where show_id = @showId";
        command.Parameters.AddWithValue("showId", showId);
        var result = await command.ExecuteScalarAsync();
        return (long)result!;
    }
}
