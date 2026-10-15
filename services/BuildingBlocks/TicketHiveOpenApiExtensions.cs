using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace BuildingBlocks;

/// <summary>
/// Registers a consistent OpenAPI document for every TicketHive service.
/// The document advertises JWT bearer authentication and only marks operations
/// as public when their endpoint metadata explicitly allows anonymous access.
/// </summary>
public static class TicketHiveOpenApiExtensions
{
    public static IServiceCollection AddTicketHiveOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    ["Bearer"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "JWT",
                        In = ParameterLocation.Header,
                        Description = "Paste an Asgardeo access token. Do not include the 'Bearer ' prefix."
                    }
                };

                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                var isAnonymous = context.Description.ActionDescriptor.EndpointMetadata
                    .OfType<IAllowAnonymous>()
                    .Any();

                if (!isAnonymous)
                {
                    operation.Security ??= [];
                    operation.Security.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = []
                    });
                }

                return Task.CompletedTask;
            });
        });

        return services;
    }
}
