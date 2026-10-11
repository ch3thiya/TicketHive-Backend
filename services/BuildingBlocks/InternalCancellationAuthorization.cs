using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks;

public static class InternalCancellationAuthorization
{
    public static IServiceCollection AddCancellationInternalAuthorization(this IServiceCollection services, string scope, string? authenticationScheme = null)
    {
        services.AddAuthorization(options => options.AddPolicy("CancellationInternal", policy =>
        {
            // A service with a dedicated internal scheme names it so user tokens never reach this policy.
            if (authenticationScheme is not null) policy.AddAuthenticationSchemes(authenticationScheme);
            policy.RequireAuthenticatedUser()
                .RequireClaim("aut", "APPLICATION")
                .RequireAssertion(context => context.User.FindAll("scope")
                    .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Contains(scope));
        }));
        return services;
    }
}
