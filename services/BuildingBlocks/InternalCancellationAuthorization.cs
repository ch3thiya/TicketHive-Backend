using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks;

public static class InternalCancellationAuthorization
{
    public static IServiceCollection AddCancellationInternalAuthorization(this IServiceCollection services, string scope)
    {
        services.AddAuthorization(options => options.AddPolicy("CancellationInternal", policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim("aut", "APPLICATION")
            .RequireAssertion(context => context.User.FindAll("scope")
                .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Contains(scope))));
        return services;
    }
}
