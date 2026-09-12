using BuildingBlocks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BuildingBlocks.Tests;

public class StartupMigrationTests
{
    private const string ConnectionString = "Host=localhost;Database=tickethive;Username=postgres;Password=postgres";

    [Fact]
    public void MigrateIfDevelopment_ProductionHost_DoesNotMigrate()
    {
        // Arrange
        var app = BuildHost(Environments.Production);
        var migrated = false;

        // Act
        DatabaseMigrator.MigrateIfDevelopment(app.Environment, app.Configuration, _ => migrated = true);

        // Assert
        Assert.False(migrated);
    }

    [Fact]
    public void MigrateIfDevelopment_DevelopmentHost_Migrates()
    {
        // Arrange
        var app = BuildHost(Environments.Development);
        string? capturedConnectionString = null;

        // Act
        DatabaseMigrator.MigrateIfDevelopment(app.Environment, app.Configuration, cs => capturedConnectionString = cs);

        // Assert
        Assert.Equal(ConnectionString, capturedConnectionString);
    }

    private static WebApplication BuildHost(string environmentName)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = environmentName;
        builder.Configuration["ConnectionStrings:DefaultConnection"] = ConnectionString;
        return builder.Build();
    }
}
