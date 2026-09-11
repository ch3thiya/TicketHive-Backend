using Microsoft.AspNetCore.Builder;

namespace BuildingBlocks;

public static class ServiceDefaultsExtensions
{
    public static WebApplicationBuilder AddServiceDefaults(this WebApplicationBuilder builder)
    {
        builder.AddOpenTelemetryDefaults();
        builder.AddHealthCheckDefaults();

        return builder;
    }
}
