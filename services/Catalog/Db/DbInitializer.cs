using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Catalog.Service.Db;

public class DbInitializer
{
    private readonly DbConnectionFactory _connectionFactory;
    private readonly ILogger<DbInitializer> _logger;

    public DbInitializer(DbConnectionFactory connectionFactory, ILogger<DbInitializer> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing Catalog database schema...");

        try
        {
            using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
            
            string ddl = @"
                CREATE EXTENSION IF NOT EXISTS ""pgcrypto"";

                CREATE TABLE IF NOT EXISTS events (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    organizer_id UUID NOT NULL,
                    name VARCHAR(255) NOT NULL,
                    description TEXT NOT NULL DEFAULT '',
                    category VARCHAR(100) NOT NULL DEFAULT '',
                    event_date DATE,
                    event_time TIME,
                    banner_url TEXT NOT NULL DEFAULT '',
                    status VARCHAR(50) NOT NULL DEFAULT 'Draft',
                    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_events_organizer_id ON events(organizer_id);
                CREATE INDEX IF NOT EXISTS idx_events_status ON events(status);

                CREATE TABLE IF NOT EXISTS shows (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    event_id UUID NOT NULL REFERENCES events(id) ON DELETE CASCADE,
                    show_date DATE NOT NULL,
                    show_time TIME NOT NULL,
                    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ticket_categories (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    show_id UUID NOT NULL REFERENCES shows(id) ON DELETE CASCADE,
                    name VARCHAR(100) NOT NULL,
                    price NUMERIC(10, 2) NOT NULL,
                    capacity INT NOT NULL,
                    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_shows_event_id ON shows(event_id);
                CREATE INDEX IF NOT EXISTS idx_ticket_categories_show_id ON ticket_categories(show_id);
            ";

            using var command = new NpgsqlCommand(ddl, connection);
            await command.ExecuteNonQueryAsync();
            _logger.LogInformation("Catalog database schema initialized successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while initializing the Catalog database.");
            throw;
        }
    }
}
