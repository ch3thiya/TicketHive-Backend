using BuildingBlocks;
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
}
