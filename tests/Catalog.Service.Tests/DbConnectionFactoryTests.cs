using System.Collections.Generic;
using System.Reflection;
using Catalog.Service.Db;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Catalog.Service.Tests;

public class DbConnectionFactoryTests
{
    [Fact]
    public void Constructor_UriConnectionString_NormalizesToKeyValueForm()
    {
        // Arrange
        var configuration = BuildConfiguration("postgresql://myuser:mypass@dbhost:6543/mydb");

        // Act
        var factory = new DbConnectionFactory(configuration);

        // Assert
        var normalized = GetNormalizedConnectionString(factory);
        Assert.Contains("Host=dbhost", normalized);
        Assert.Contains("Port=6543", normalized);
        Assert.Contains("Database=mydb", normalized);
        Assert.Contains("Username=myuser", normalized);
    }

    [Fact]
    public void Constructor_KeyValueConnectionString_PassesThrough()
    {
        // Arrange
        var keyValueConnectionString = "Host=localhost;Database=tickethive_catalog;Username=postgres;Password=postgres";
        var configuration = BuildConfiguration(keyValueConnectionString);

        // Act
        var factory = new DbConnectionFactory(configuration);

        // Assert
        var normalized = GetNormalizedConnectionString(factory);
        Assert.Equal(keyValueConnectionString, normalized);
    }

    private static IConfiguration BuildConfiguration(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString
            })
            .Build();

    private static string GetNormalizedConnectionString(DbConnectionFactory factory)
    {
        var field = typeof(DbConnectionFactory).GetField("_connectionString", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (string)field.GetValue(factory)!;
    }
}
