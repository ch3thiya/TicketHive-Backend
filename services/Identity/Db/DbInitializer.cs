using Npgsql;
using Microsoft.Extensions.Logging;
using System.IO;

namespace Identity.Service.Db;

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
        _logger.LogInformation("Initializing database schema...");

        try
        {
            using var connection = (NpgsqlConnection)await _connectionFactory.CreateConnectionAsync();
            
            // Define DDL SQL to create tables if they do not exist
            string ddl = @"
                CREATE EXTENSION IF NOT EXISTS ""uuid-ossp"";

                CREATE TABLE IF NOT EXISTS user_accounts (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    wso2_sub VARCHAR(255) UNIQUE NOT NULL,
                    email VARCHAR(255) UNIQUE NOT NULL,
                    full_name VARCHAR(255) NOT NULL,
                    role VARCHAR(50) NOT NULL,
                    approval_status VARCHAR(50) NOT NULL,
                    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL
                );

                CREATE TABLE IF NOT EXISTS organizer_requests (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    user_account_id UUID REFERENCES user_accounts(id) ON DELETE CASCADE,
                    organization_name VARCHAR(255) NOT NULL,
                    business_email VARCHAR(255) NOT NULL,
                    phone VARCHAR(50) NOT NULL,
                    event_type VARCHAR(255) NOT NULL,
                    about TEXT NOT NULL,
                    status VARCHAR(50) NOT NULL DEFAULT 'pending',
                    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL,
                    reviewed_at TIMESTAMP WITH TIME ZONE
                );

                CREATE INDEX IF NOT EXISTS idx_user_accounts_wso2_sub ON user_accounts(wso2_sub);
            ";

            using var command = new NpgsqlCommand(ddl, connection);
            await command.ExecuteNonQueryAsync();
            _logger.LogInformation("Database schema initialized successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while initializing the database.");
            throw;
        }
    }
}
