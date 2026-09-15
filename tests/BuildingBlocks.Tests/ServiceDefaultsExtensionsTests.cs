using BuildingBlocks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuildingBlocks.Tests;

public class ServiceDefaultsExtensionsTests
{
    [Fact]
    public void AddServiceDefaults_ResolvesTimeProvider_AsSystemTimeProvider()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.AddServiceDefaults();

        // Act
        using var app = builder.Build();
        var timeProvider = app.Services.GetRequiredService<TimeProvider>();

        // Assert
        Assert.Same(TimeProvider.System, timeProvider);
    }
}
