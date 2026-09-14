using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using Catalog.Service.Models;

namespace Catalog.Service.Db;

public class VenueRepository : IVenueRepository
{
    private readonly DbConnectionFactory _connectionFactory;

    public VenueRepository(DbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<Venue> CreateVenueAsync(Venue venue)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            INSERT INTO venues (id, name, address, capacity, created_at, updated_at)
            VALUES (@Id, @Name, @Address, @Capacity, @CreatedAt, @UpdatedAt);
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", venue.Id);
        command.Parameters.AddWithValue("Name", venue.Name);
        command.Parameters.AddWithValue("Address", venue.Address);
        command.Parameters.AddWithValue("Capacity", venue.Capacity);
        command.Parameters.AddWithValue("CreatedAt", venue.CreatedAt);
        command.Parameters.AddWithValue("UpdatedAt", venue.UpdatedAt);

        await command.ExecuteNonQueryAsync();
        return venue;
    }

    public async Task<Venue?> GetVenueByIdAsync(Guid id)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT id, name, address, capacity, created_at, updated_at
            FROM venues
            WHERE id = @Id;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", id);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return MapVenue(reader);
        }

        return null;
    }

    public async Task<bool> VenueExistsAsync(Guid id)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = "SELECT 1 FROM venues WHERE id = @Id;";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", id);

        var result = await command.ExecuteScalarAsync();
        return result != null;
    }

    public async Task<List<Venue>> GetAllVenuesAsync()
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT id, name, address, capacity, created_at, updated_at
            FROM venues
            ORDER BY name ASC;
        ";

        using var command = new NpgsqlCommand(sql, connection);

        using var reader = await command.ExecuteReaderAsync();
        var list = new List<Venue>();
        while (await reader.ReadAsync())
        {
            list.Add(MapVenue(reader));
        }

        return list;
    }

    public async Task UpdateVenueAsync(Venue venue)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            UPDATE venues
            SET name = @Name,
                address = @Address,
                capacity = @Capacity,
                updated_at = @UpdatedAt
            WHERE id = @Id;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", venue.Id);
        command.Parameters.AddWithValue("Name", venue.Name);
        command.Parameters.AddWithValue("Address", venue.Address);
        command.Parameters.AddWithValue("Capacity", venue.Capacity);
        command.Parameters.AddWithValue("UpdatedAt", venue.UpdatedAt);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<VenueDeleteResult?> DeleteVenueIfUnusedAsync(Guid id)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        using var transaction = await connection.BeginTransactionAsync();

        try
        {
            // Lock the venue row first. A show insert/update referencing this
            // venue needs a FOR KEY SHARE lock on the same row to satisfy the
            // shows.venue_id foreign key, so it blocks until this transaction
            // ends — closing the gap between the count below and the delete.
            const string lockSql = "SELECT 1 FROM venues WHERE id = @Id FOR UPDATE;";
            using (var lockCmd = new NpgsqlCommand(lockSql, connection, transaction))
            {
                lockCmd.Parameters.AddWithValue("Id", id);
                var found = await lockCmd.ExecuteScalarAsync();
                if (found == null)
                {
                    await transaction.RollbackAsync();
                    return null;
                }
            }

            const string countSql = "SELECT COUNT(*) FROM shows WHERE venue_id = @Id;";
            int referencingShows;
            using (var countCmd = new NpgsqlCommand(countSql, connection, transaction))
            {
                countCmd.Parameters.AddWithValue("Id", id);
                referencingShows = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
            }

            if (referencingShows > 0)
            {
                await transaction.RollbackAsync();
                return new VenueDeleteResult(false, referencingShows);
            }

            const string deleteSql = "DELETE FROM venues WHERE id = @Id;";
            using (var deleteCmd = new NpgsqlCommand(deleteSql, connection, transaction))
            {
                deleteCmd.Parameters.AddWithValue("Id", id);
                await deleteCmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            return new VenueDeleteResult(true, 0);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // Backstop: the lock above should prevent this, but if a show
            // reference still slipped in, refuse the delete instead of a 500.
            await transaction.RollbackAsync();
            var referencingShows = await CountReferencingShowsAsync(id);
            return new VenueDeleteResult(false, referencingShows);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private async Task<int> CountReferencingShowsAsync(Guid id)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = "SELECT COUNT(*) FROM shows WHERE venue_id = @Id;";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", id);

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static Venue MapVenue(NpgsqlDataReader reader)
    {
        return new Venue
        {
            Id = reader.GetGuid(0),
            Name = reader.GetString(1),
            Address = reader.GetString(2),
            Capacity = reader.GetInt32(3),
            CreatedAt = reader.GetDateTime(4),
            UpdatedAt = reader.GetDateTime(5)
        };
    }
}
