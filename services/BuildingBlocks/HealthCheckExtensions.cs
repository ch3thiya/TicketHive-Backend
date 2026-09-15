using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BuildingBlocks;

internal static class HealthCheckExtensions
{
    public static WebApplicationBuilder AddHealthCheckDefaults(this WebApplicationBuilder builder)
    {
        var healthChecks = builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            healthChecks.AddCheck(
                "database",
                new NpgsqlConnectivityHealthCheck(connectionString),
                tags: ["ready"]);
        }

        return builder;
    }
}
