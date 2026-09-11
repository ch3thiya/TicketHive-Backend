using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BuildingBlocks;

public static class ServiceDefaultsExtensions
{
    public static WebApplicationBuilder AddServiceDefaults(this WebApplicationBuilder builder)
    {
        builder.AddOpenTelemetryDefaults();
        builder.AddHealthCheckDefaults();
        builder.AddHttpClientResilienceDefaults();

        builder.Services.AddProblemDetails();
        builder.Services.TryAddSingleton(TimeProvider.System);

        return builder;
    }
}
