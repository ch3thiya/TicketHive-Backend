using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BuildingBlocks;

public static class EndpointExtensions
{
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        var liveOptions = new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("live")
        };

        app.MapHealthChecks("/health/live", liveOptions).AllowAnonymous();

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = _ => true
        }).AllowAnonymous();

        // Kept so existing Azure probes keep working until DevOps switches them to /health/live.
        app.MapHealthChecks("/health", liveOptions).AllowAnonymous();

        return app;
    }
}
