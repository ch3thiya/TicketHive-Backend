using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace BuildingBlocks;

internal static class HttpClientResilienceExtensions
{
    public static WebApplicationBuilder AddHttpClientResilienceDefaults(this WebApplicationBuilder builder)
    {
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler(options =>
            {
                options.Retry.DisableForUnsafeHttpMethods();
            });
        });

        return builder;
    }
}
