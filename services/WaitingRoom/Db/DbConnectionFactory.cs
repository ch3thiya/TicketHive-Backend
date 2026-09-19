using System;
using System.Data;
using System.Threading.Tasks;
using BuildingBlocks;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace WaitingRoom.Service.Db;

public class DbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(IConfiguration configuration)
    {
        var rawConnectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new ArgumentException("Connection string 'DefaultConnection' is missing from configuration.");

        _connectionString = PostgresConnectionString.Normalize(rawConnectionString);
    }

    public async Task<IDbConnection> CreateConnectionAsync()
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }
}
