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
