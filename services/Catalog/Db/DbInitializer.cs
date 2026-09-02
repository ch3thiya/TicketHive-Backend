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

        int maxRetries = 5;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();

                // 1. Try extension creation optionally (pgcrypto is built into Postgres 13+ gen_random_uuid)
                try
                {
                    using var extCmd = new NpgsqlCommand(@"CREATE EXTENSION IF NOT EXISTS ""pgcrypto"";", connection);
                    await extCmd.ExecuteNonQueryAsync();
                }
                catch (Exception extEx)
                {
                    _logger.LogWarning(extEx, "Extension creation skipped (pgcrypto may already exist or require superuser).");
                }

                // 2. Execute table and index creation DDL
                string ddl = @"
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
                        cancellation_cutoff_hours INT,
                        created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_events_organizer_id ON events(organizer_id);
                    CREATE INDEX IF NOT EXISTS idx_events_status ON events(status);

                    CREATE TABLE IF NOT EXISTS shows (
                        id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                        event_id UUID NOT NULL REFERENCES events(id) ON DELETE CASCADE,
                        show_date DATE NOT NULL,
                        show_time TIME NOT NULL,
                        venue_id UUID,
                        on_sale_at TIMESTAMP WITH TIME ZONE,
                        high_demand_threshold INT,
                        reminder_minutes_before INT,
                        status VARCHAR(50) NOT NULL DEFAULT 'Active',
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
                    CREATE INDEX IF NOT EXISTS idx_shows_status ON shows(status);
                    CREATE INDEX IF NOT EXISTS idx_ticket_categories_show_id ON ticket_categories(show_id);

                    -- Migration alter statements for backwards compatibility
                    ALTER TABLE events ADD COLUMN IF NOT EXISTS cancellation_cutoff_hours INT;
                    ALTER TABLE shows ADD COLUMN IF NOT EXISTS venue_id UUID;
                    ALTER TABLE shows ADD COLUMN IF NOT EXISTS on_sale_at TIMESTAMP WITH TIME ZONE;
                    ALTER TABLE shows ADD COLUMN IF NOT EXISTS high_demand_threshold INT;
                    ALTER TABLE shows ADD COLUMN IF NOT EXISTS reminder_minutes_before INT;
                ";

                using var command = new NpgsqlCommand(ddl, connection);
                await command.ExecuteNonQueryAsync();
                _logger.LogInformation("Catalog database schema initialized successfully.");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Attempt {Attempt}/{MaxRetries} failed to initialize Catalog database schema.", attempt, maxRetries);
                if (attempt == maxRetries)
                {
                    _logger.LogError(ex, "All attempts failed to initialize the Catalog database.");
                    throw;
                }
                await Task.Delay(2000);
            }
        }
    }
}
