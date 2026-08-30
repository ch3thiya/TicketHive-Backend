using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using Catalog.Service.Models;

namespace Catalog.Service.Db;

public class EventRepository : IEventRepository
{
    private readonly DbConnectionFactory _connectionFactory;

    public EventRepository(DbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<Event> CreateEventAsync(Event evt)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        if (evt.Id == Guid.Empty)
        {
            evt.Id = Guid.NewGuid();
        }

        const string sql = @"
            INSERT INTO events (id, organizer_id, name, description, category, event_date, event_time, banner_url, status, cancellation_cutoff_hours)
            VALUES (@Id, @OrganizerId, @Name, @Description, @Category, @EventDate, @EventTime, @BannerUrl, @Status, @CancellationCutoffHours)
            RETURNING id, created_at;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", evt.Id);
        command.Parameters.AddWithValue("OrganizerId", evt.OrganizerId);
        command.Parameters.AddWithValue("Name", evt.Name);
        command.Parameters.AddWithValue("Description", evt.Description);
        command.Parameters.AddWithValue("Category", evt.Category);
        command.Parameters.AddWithValue("EventDate", (object?)evt.EventDate ?? DBNull.Value);
        command.Parameters.AddWithValue("EventTime", (object?)evt.EventTime ?? DBNull.Value);
        command.Parameters.AddWithValue("BannerUrl", evt.BannerUrl);
        command.Parameters.AddWithValue("Status", evt.Status);
        command.Parameters.AddWithValue("CancellationCutoffHours", (object?)evt.CancellationCutoffHours ?? DBNull.Value);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            evt.CreatedAt = reader.GetDateTime(1);
        }

        return evt;
    }

    public async Task<Event?> GetEventByIdAsync(Guid id)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT id, organizer_id, name, description, category, event_date, event_time, banner_url, status, cancellation_cutoff_hours, created_at
            FROM events
            WHERE id = @Id;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", id);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return MapEvent(reader);
        }

        return null;
    }

    public async Task<List<Event>> GetEventsByOrganizerIdAsync(Guid organizerId)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT id, organizer_id, name, description, category, event_date, event_time, banner_url, status, cancellation_cutoff_hours, created_at
            FROM events
            WHERE organizer_id = @OrganizerId
            ORDER BY created_at DESC;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("OrganizerId", organizerId);

        using var reader = await command.ExecuteReaderAsync();
        var list = new List<Event>();
        while (await reader.ReadAsync())
        {
            list.Add(MapEvent(reader));
        }

        return list;
    }

    public async Task<List<Event>> GetAllPublishedEventsAsync()
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT id, organizer_id, name, description, category, event_date, event_time, banner_url, status, cancellation_cutoff_hours, created_at
            FROM events
            WHERE status = 'Published'
            ORDER BY created_at DESC;
        ";

        using var command = new NpgsqlCommand(sql, connection);

        using var reader = await command.ExecuteReaderAsync();
        var list = new List<Event>();
        while (await reader.ReadAsync())
        {
            list.Add(MapEvent(reader));
        }

        return list;
    }

    public async Task UpdateEventAsync(Event evt)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            UPDATE events
            SET name = @Name,
                description = @Description,
                category = @Category,
                event_date = @EventDate,
                event_time = @EventTime,
                banner_url = @BannerUrl,
                status = @Status,
                cancellation_cutoff_hours = @CancellationCutoffHours
            WHERE id = @Id;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", evt.Id);
        command.Parameters.AddWithValue("Name", evt.Name);
        command.Parameters.AddWithValue("Description", evt.Description);
        command.Parameters.AddWithValue("Category", evt.Category);
        command.Parameters.AddWithValue("EventDate", (object?)evt.EventDate ?? DBNull.Value);
        command.Parameters.AddWithValue("EventTime", (object?)evt.EventTime ?? DBNull.Value);
        command.Parameters.AddWithValue("BannerUrl", evt.BannerUrl);
        command.Parameters.AddWithValue("Status", evt.Status);
        command.Parameters.AddWithValue("CancellationCutoffHours", (object?)evt.CancellationCutoffHours ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdateEventStatusAsync(Guid eventId, string status)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            UPDATE events
            SET status = @Status
            WHERE id = @Id;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", eventId);
        command.Parameters.AddWithValue("Status", status);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<Show> CreateShowWithCategoriesAsync(Show show, List<TicketCategory> categories)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        using var transaction = await connection.BeginTransactionAsync();

        try
        {
            if (show.Id == Guid.Empty)
            {
                show.Id = Guid.NewGuid();
            }

            const string showSql = @"
                INSERT INTO shows (id, event_id, show_date, show_time, venue_id, on_sale_at, high_demand_threshold, reminder_minutes_before, status)
                VALUES (@Id, @EventId, @ShowDate, @ShowTime, @VenueId, @OnSaleAt, @HighDemandThreshold, @ReminderMinutesBefore, @Status)
                RETURNING created_at;
            ";

            using (var showCmd = new NpgsqlCommand(showSql, connection, transaction))
            {
                showCmd.Parameters.AddWithValue("Id", show.Id);
                showCmd.Parameters.AddWithValue("EventId", show.EventId);
                showCmd.Parameters.AddWithValue("ShowDate", show.ShowDate);
                showCmd.Parameters.AddWithValue("ShowTime", show.ShowTime);
                showCmd.Parameters.AddWithValue("VenueId", (object?)show.VenueId ?? DBNull.Value);
                showCmd.Parameters.AddWithValue("OnSaleAt", (object?)show.OnSaleAt ?? DBNull.Value);
                showCmd.Parameters.AddWithValue("HighDemandThreshold", (object?)show.HighDemandThreshold ?? DBNull.Value);
                showCmd.Parameters.AddWithValue("ReminderMinutesBefore", (object?)show.ReminderMinutesBefore ?? DBNull.Value);
                showCmd.Parameters.AddWithValue("Status", show.Status);

                using var reader = await showCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    show.CreatedAt = reader.GetDateTime(0);
                }
            }

            const string categorySql = @"
                INSERT INTO ticket_categories (id, show_id, name, price, capacity)
                VALUES (@Id, @ShowId, @Name, @Price, @Capacity)
                RETURNING created_at;
            ";

            foreach (var category in categories)
            {
                if (category.Id == Guid.Empty)
                {
                    category.Id = Guid.NewGuid();
                }
                category.ShowId = show.Id;

                using var catCmd = new NpgsqlCommand(categorySql, connection, transaction);
                catCmd.Parameters.AddWithValue("Id", category.Id);
                catCmd.Parameters.AddWithValue("ShowId", category.ShowId);
                catCmd.Parameters.AddWithValue("Name", category.Name);
                catCmd.Parameters.AddWithValue("Price", category.Price);
                catCmd.Parameters.AddWithValue("Capacity", category.Capacity);

                using var catReader = await catCmd.ExecuteReaderAsync();
                if (await catReader.ReadAsync())
                {
                    category.CreatedAt = catReader.GetDateTime(0);
                }
            }

            await transaction.CommitAsync();
            return show;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<Show?> GetShowByIdAsync(Guid showId)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT id, event_id, show_date, show_time, venue_id, on_sale_at, high_demand_threshold, reminder_minutes_before, status, created_at
            FROM shows
            WHERE id = @Id;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", showId);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return MapShow(reader);
        }

        return null;
    }

    public async Task<List<Show>> GetShowsByEventIdAsync(Guid eventId)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT id, event_id, show_date, show_time, venue_id, on_sale_at, high_demand_threshold, reminder_minutes_before, status, created_at
            FROM shows
            WHERE event_id = @EventId AND status != 'Cancelled'
            ORDER BY show_date ASC, show_time ASC;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("EventId", eventId);

        using var reader = await command.ExecuteReaderAsync();
        var list = new List<Show>();
        while (await reader.ReadAsync())
        {
            list.Add(MapShow(reader));
        }

        return list;
    }

    public async Task<List<TicketCategory>> GetTicketCategoriesByShowIdAsync(Guid showId)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT id, show_id, name, price, capacity, created_at
            FROM ticket_categories
            WHERE show_id = @ShowId
            ORDER BY price ASC;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ShowId", showId);

        using var reader = await command.ExecuteReaderAsync();
        var list = new List<TicketCategory>();
        while (await reader.ReadAsync())
        {
            list.Add(new TicketCategory
            {
                Id = reader.GetGuid(0),
                ShowId = reader.GetGuid(1),
                Name = reader.GetString(2),
                Price = reader.GetDecimal(3),
                Capacity = reader.GetInt32(4),
                CreatedAt = reader.GetDateTime(5)
            });
        }

        return list;
    }

    public async Task UpdateShowAsync(Show show)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            UPDATE shows
            SET show_date = @ShowDate,
                show_time = @ShowTime,
                venue_id = @VenueId,
                on_sale_at = @OnSaleAt,
                high_demand_threshold = @HighDemandThreshold,
                reminder_minutes_before = @ReminderMinutesBefore,
                status = @Status
            WHERE id = @Id;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", show.Id);
        command.Parameters.AddWithValue("ShowDate", show.ShowDate);
        command.Parameters.AddWithValue("ShowTime", show.ShowTime);
        command.Parameters.AddWithValue("VenueId", (object?)show.VenueId ?? DBNull.Value);
        command.Parameters.AddWithValue("OnSaleAt", (object?)show.OnSaleAt ?? DBNull.Value);
        command.Parameters.AddWithValue("HighDemandThreshold", (object?)show.HighDemandThreshold ?? DBNull.Value);
        command.Parameters.AddWithValue("ReminderMinutesBefore", (object?)show.ReminderMinutesBefore ?? DBNull.Value);
        command.Parameters.AddWithValue("Status", show.Status);

        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdateShowStatusAsync(Guid showId, string status)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            UPDATE shows
            SET status = @Status
            WHERE id = @Id;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", showId);
        command.Parameters.AddWithValue("Status", status);

        await command.ExecuteNonQueryAsync();
    }

    private static Event MapEvent(NpgsqlDataReader reader)
    {
        return new Event
        {
            Id = reader.GetGuid(0),
            OrganizerId = reader.GetGuid(1),
            Name = reader.GetString(2),
            Description = reader.GetString(3),
            Category = reader.GetString(4),
            EventDate = reader.IsDBNull(5) ? null : DateOnly.FromDateTime(reader.GetDateTime(5)),
            EventTime = reader.IsDBNull(6) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(6)),
            BannerUrl = reader.GetString(7),
            Status = reader.GetString(8),
            CancellationCutoffHours = reader.IsDBNull(9) ? null : reader.GetInt32(9),
            CreatedAt = reader.GetDateTime(10)
        };
    }

    private static Show MapShow(NpgsqlDataReader reader)
    {
        return new Show
        {
            Id = reader.GetGuid(0),
            EventId = reader.GetGuid(1),
            ShowDate = DateOnly.FromDateTime(reader.GetDateTime(2)),
            ShowTime = TimeOnly.FromTimeSpan(reader.GetTimeSpan(3)),
            VenueId = reader.IsDBNull(4) ? null : reader.GetGuid(4),
            OnSaleAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
            HighDemandThreshold = reader.IsDBNull(6) ? null : reader.GetInt32(6),
            ReminderMinutesBefore = reader.IsDBNull(7) ? null : reader.GetInt32(7),
            Status = reader.GetString(8),
            CreatedAt = reader.GetDateTime(9)
        };
    }
}
