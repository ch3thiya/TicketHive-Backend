using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BuildingBlocks;

public static class InternalServiceTokenClientExtensions
{
    /// <summary>
    /// Registers the client-credentials token client and the delegating
    /// handler that attaches it to outgoing internal calls. The consuming
    /// service adds InternalServiceAuthenticationHandler to its own typed
    /// client with AddHttpMessageHandler.
    /// </summary>
    public static IServiceCollection AddInternalServiceTokenClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<InternalServiceTokenClientOptions>, InternalServiceTokenClientOptionsValidator>();
        services.AddOptions<InternalServiceTokenClientOptions>()
            .Bind(configuration.GetSection(InternalServiceTokenClientOptions.SectionName))
            .ValidateOnStart();

        services.AddHttpClient<IInternalServiceTokenClient, InternalServiceTokenClient>();
        services.AddTransient<InternalServiceAuthenticationHandler>();

        return services;
    }
}
