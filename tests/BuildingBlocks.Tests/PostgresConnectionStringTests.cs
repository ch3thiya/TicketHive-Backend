using BuildingBlocks;
using Npgsql;
using Xunit;

namespace BuildingBlocks.Tests;

public class PostgresConnectionStringTests
{
    [Fact]
    public void Normalize_UriForm_ConvertsToKeyValueForm()
    {
        // Arrange
        var uriConnectionString = "postgresql://myuser:mypass@dbhost:6543/mydb";

        // Act
        var result = PostgresConnectionString.Normalize(uriConnectionString);

        // Assert
        Assert.Contains("Host=dbhost", result);
        Assert.Contains("Port=6543", result);
        Assert.Contains("Database=mydb", result);
        Assert.Contains("Username=myuser", result);
        Assert.Contains("Password=mypass", result);
    }

    [Fact]
    public void Normalize_KeyValueForm_PassesThrough()
    {
        // Arrange
        var keyValueConnectionString =
            "Host=localhost;Database=tickethive_catalog;Username=postgres;Password=postgres";

        // Act
        var result = PostgresConnectionString.Normalize(keyValueConnectionString);

        // Assert
        Assert.Equal(keyValueConnectionString, result);
    }

    [Fact]
    public void Normalize_UriWithoutPasswordOrPort_UsesEmptyPasswordAndDefaultPort()
    {
        var result = PostgresConnectionString.Normalize("postgres://myuser@dbhost/mydb");

        Assert.Contains("Host=dbhost", result);
        Assert.Contains("Port=5432", result);
        Assert.Contains("Username=myuser", result);
        Assert.Null(new NpgsqlConnectionStringBuilder(result).Password);
    }

    [Fact]
    public void Normalize_EscapedCredentials_AreDecoded()
    {
        var result = PostgresConnectionString.Normalize("postgresql://first%40last:p%40ss@dbhost/mydb");

        Assert.Contains("Username=first@last", result);
        Assert.Contains("Password=p@ss", result);
    }
}
