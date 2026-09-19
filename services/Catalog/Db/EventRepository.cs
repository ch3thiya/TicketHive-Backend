using System;
using System.Collections.Generic;
using System.Linq;
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

    public async Task<List<Event>> GetPublishedEventsAsync(string? search, string? category, DateOnly? fromDate, DateOnly? toDate, Guid? venueId)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        var sql = @"
            SELECT DISTINCT e.id, e.organizer_id, e.name, e.description, e.category, e.event_date, e.event_time, e.banner_url, e.status, e.cancellation_cutoff_hours, e.created_at
            FROM events e";

        // JOIN to shows only when filtering by show-level fields (date range or venue)
        bool joinShows = fromDate.HasValue || toDate.HasValue || venueId.HasValue;
        if (joinShows)
        {
            sql += @"
            INNER JOIN shows s ON s.event_id = e.id AND s.status != 'Cancelled'";
        }

        sql += @"
            WHERE e.status = 'Published'";

        var parameters = new List<NpgsqlParameter>();

        if (!string.IsNullOrWhiteSpace(search))
        {
            sql += " AND e.name ILIKE @Search";
            parameters.Add(new NpgsqlParameter("Search", $"%{search.Trim()}%"));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            var rawCategory = category.Trim();
            var stemmedCategory = rawCategory.TrimEnd('s', 'S');

            sql += " AND (e.category ILIKE @CategoryRaw OR e.category ILIKE @CategoryStemmed)";
            parameters.Add(new NpgsqlParameter("CategoryRaw", $"%{rawCategory}%"));
            parameters.Add(new NpgsqlParameter("CategoryStemmed", $"%{stemmedCategory}%"));
        }

        if (fromDate.HasValue)
        {
            sql += " AND s.show_date >= @FromDate";
            parameters.Add(new NpgsqlParameter("FromDate", fromDate.Value));
        }

        if (toDate.HasValue)
        {
            sql += " AND s.show_date <= @ToDate";
            parameters.Add(new NpgsqlParameter("ToDate", toDate.Value));
        }

        if (venueId.HasValue)
        {
            sql += " AND s.venue_id = @VenueId";
            parameters.Add(new NpgsqlParameter("VenueId", venueId.Value));
        }

        sql += @"
            ORDER BY e.created_at DESC;";

        using var command = new NpgsqlCommand(sql, connection);
        foreach (var param in parameters)
        {
            command.Parameters.Add(param);
        }

        using var reader = await command.ExecuteReaderAsync();
        var list = new List<Event>();
        while (await reader.ReadAsync())
        {
            list.Add(MapEvent(reader));
        }

        return list;
    }

    public async Task<Event?> GetPublishedEventByIdAsync(Guid id)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT id, organizer_id, name, description, category, event_date, event_time, banner_url, status, cancellation_cutoff_hours, created_at
            FROM events
            WHERE id = @Id AND status = 'Published';
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
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // Backstop for the service-layer venue check above: closes the
            // race where the venue was deleted between that check and this
            // write, instead of surfacing a raw database error as a 500.
            await transaction.RollbackAsync();
            throw new ArgumentException($"Venue '{show.VenueId}' does not exist.", nameof(show.VenueId));
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

    public async Task<Dictionary<Guid, List<Show>>> GetShowsByEventIdsAsync(IReadOnlyCollection<Guid> eventIds)
    {
        var result = new Dictionary<Guid, List<Show>>();
        if (eventIds.Count == 0)
        {
            return result;
        }

        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT id, event_id, show_date, show_time, venue_id, on_sale_at, high_demand_threshold, reminder_minutes_before, status, created_at
            FROM shows
            WHERE event_id = ANY(@EventIds) AND status != 'Cancelled'
            ORDER BY show_date ASC, show_time ASC;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("EventIds", eventIds.ToArray());

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var show = MapShow(reader);
            if (!result.TryGetValue(show.EventId, out var shows))
            {
                shows = new List<Show>();
                result[show.EventId] = shows;
            }

            shows.Add(show);
        }

        return result;
    }

    public async Task<List<TicketCategory>> GetTicketCategoriesByShowIdAsync(Guid showId)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT id, show_id, name, price, capacity, is_active, created_at
            FROM ticket_categories
            WHERE show_id = @ShowId AND is_active = true
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
                IsActive = reader.GetBoolean(5),
                CreatedAt = reader.GetDateTime(6)
            });
        }

        return list;
    }

    public async Task<Dictionary<Guid, List<TicketCategory>>> GetTicketCategoriesByShowIdsAsync(IReadOnlyCollection<Guid> showIds)
    {
        var result = new Dictionary<Guid, List<TicketCategory>>();
        if (showIds.Count == 0)
        {
            return result;
        }

        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

        const string sql = @"
            SELECT id, show_id, name, price, capacity, is_active, created_at
            FROM ticket_categories
            WHERE show_id = ANY(@ShowIds) AND is_active = true
            ORDER BY price ASC;
        ";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ShowIds", showIds.ToArray());

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var category = new TicketCategory
            {
                Id = reader.GetGuid(0),
                ShowId = reader.GetGuid(1),
                Name = reader.GetString(2),
                Price = reader.GetDecimal(3),
                Capacity = reader.GetInt32(4),
                IsActive = reader.GetBoolean(5),
                CreatedAt = reader.GetDateTime(6)
            };

            if (!result.TryGetValue(category.ShowId, out var categories))
            {
                categories = new List<TicketCategory>();
                result[category.ShowId] = categories;
            }

            categories.Add(category);
        }

        return result;
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

        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // Backstop for the service-layer venue check above: closes the
            // race where the venue was deleted between that check and this
            // write, instead of surfacing a raw database error as a 500.
            throw new ArgumentException($"Venue '{show.VenueId}' does not exist.", nameof(show.VenueId));
        }
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

    public async Task SaveTicketCategoriesAsync(Guid showId, List<TicketCategory> categories)
    {
        using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        using var transaction = await connection.BeginTransactionAsync();

        try
        {
            // Lock every row (active or retired) this show currently owns, so a
            // concurrent reconcile of the same show can't race this one.
            var existing = new Dictionary<Guid, bool>();
            const string lockSql = @"
                SELECT id, is_active
                FROM ticket_categories
                WHERE show_id = @ShowId
                FOR UPDATE;
            ";
            using (var lockCmd = new NpgsqlCommand(lockSql, connection, transaction))
            {
                lockCmd.Parameters.AddWithValue("ShowId", showId);
                using var reader = await lockCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    existing[reader.GetGuid(0)] = reader.GetBoolean(1);
                }
            }

            var toUpdate = new List<TicketCategory>();
            var toInsert = new List<TicketCategory>();

            foreach (var category in categories)
            {
                if (category.Id == Guid.Empty)
                {
                    category.Id = Guid.CreateVersion7();
                    category.ShowId = showId;
                    toInsert.Add(category);
                    continue;
                }

                if (!existing.TryGetValue(category.Id, out var isActive))
                {
                    throw new ArgumentException($"Ticket category '{category.Id}' does not belong to this show.");
                }

                if (!isActive)
                {
                    throw new ArgumentException($"Ticket category '{category.Id}' has been retired and cannot be reused.");
                }

                category.ShowId = showId;
                toUpdate.Add(category);
            }

            if (toUpdate.Count > 0)
            {
                const string updateSql = @"
                    UPDATE ticket_categories AS t
                    SET name = v.name, price = v.price, capacity = v.capacity
                    FROM unnest(@Ids, @Names, @Prices, @Capacities) AS v(id, name, price, capacity)
                    WHERE t.id = v.id AND t.show_id = @ShowId;
                ";
                using var updateCmd = new NpgsqlCommand(updateSql, connection, transaction);
                updateCmd.Parameters.AddWithValue("ShowId", showId);
                updateCmd.Parameters.AddWithValue("Ids", toUpdate.Select(c => c.Id).ToArray());
                updateCmd.Parameters.AddWithValue("Names", toUpdate.Select(c => c.Name).ToArray());
                updateCmd.Parameters.AddWithValue("Prices", toUpdate.Select(c => c.Price).ToArray());
                updateCmd.Parameters.AddWithValue("Capacities", toUpdate.Select(c => c.Capacity).ToArray());
                await updateCmd.ExecuteNonQueryAsync();
            }

            if (toInsert.Count > 0)
            {
                const string insertSql = @"
                    INSERT INTO ticket_categories (id, show_id, name, price, capacity)
                    SELECT id, @ShowId, name, price, capacity
                    FROM unnest(@Ids, @Names, @Prices, @Capacities) AS v(id, name, price, capacity)
                    RETURNING id, created_at;
                ";
                using var insertCmd = new NpgsqlCommand(insertSql, connection, transaction);
                insertCmd.Parameters.AddWithValue("ShowId", showId);
                insertCmd.Parameters.AddWithValue("Ids", toInsert.Select(c => c.Id).ToArray());
                insertCmd.Parameters.AddWithValue("Names", toInsert.Select(c => c.Name).ToArray());
                insertCmd.Parameters.AddWithValue("Prices", toInsert.Select(c => c.Price).ToArray());
                insertCmd.Parameters.AddWithValue("Capacities", toInsert.Select(c => c.Capacity).ToArray());

                using var reader = await insertCmd.ExecuteReaderAsync();
                var createdAtById = new Dictionary<Guid, DateTime>();
                while (await reader.ReadAsync())
                {
                    createdAtById[reader.GetGuid(0)] = reader.GetDateTime(1);
                }

                foreach (var category in toInsert)
                {
                    category.CreatedAt = createdAtById[category.Id];
                }
            }

            var keepIds = categories.Select(c => c.Id).ToArray();
            const string retireSql = @"
                UPDATE ticket_categories
                SET is_active = false
                WHERE show_id = @ShowId AND is_active = true AND id <> ALL(@KeepIds);
            ";
            using (var retireCmd = new NpgsqlCommand(retireSql, connection, transaction))
            {
                retireCmd.Parameters.AddWithValue("ShowId", showId);
                retireCmd.Parameters.AddWithValue("KeepIds", keepIds);
                await retireCmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
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

    public async Task DeleteEventAsync(Guid eventId)
    {
        await using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        const string deleteCategoriesSql = @"
            DELETE FROM ticket_categories
            WHERE show_id IN (SELECT id FROM shows WHERE event_id = @EventId);
        ";
        await using (var cmd = new NpgsqlCommand(deleteCategoriesSql, connection, transaction))
        {
            cmd.Parameters.AddWithValue("EventId", eventId);
            await cmd.ExecuteNonQueryAsync();
        }

        const string deleteShowsSql = @"
            DELETE FROM shows WHERE event_id = @EventId;
        ";
        await using (var cmd = new NpgsqlCommand(deleteShowsSql, connection, transaction))
        {
            cmd.Parameters.AddWithValue("EventId", eventId);
            await cmd.ExecuteNonQueryAsync();
        }

        const string deleteEventSql = @"
            DELETE FROM events WHERE id = @EventId;
        ";
        await using (var cmd = new NpgsqlCommand(deleteEventSql, connection, transaction))
        {
            cmd.Parameters.AddWithValue("EventId", eventId);
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
}
