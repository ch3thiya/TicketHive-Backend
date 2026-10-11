using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Booking.Service.Authorization;

public static class BookingJwtAuthentication
{
    /// <summary>
    /// Registers the user scheme (default) and the internal machine-to-machine scheme.
    /// Both validate issuer, lifetime, signature, token type and a strict audience allowlist.
    /// Startup fails when the allowlists are missing, malformed or overlap.
    /// </summary>
    public static IServiceCollection AddBookingJwtAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(BookingJwtSettings.UserScheme)
            .AddJwtBearer(BookingJwtSettings.UserScheme, _ => { })
            .AddJwtBearer(BookingJwtSettings.InternalScheme, _ => { });

        // Settings are read when the options are first built, after all configuration sources
        // are in place; the startup filter forces that (and the validation) at host start.
        services.AddOptions<JwtBearerOptions>(BookingJwtSettings.UserScheme)
            .Configure<IConfiguration, IHostEnvironment>((options, configuration, environment) =>
            {
                var settings = BookingJwtSettings.Load(configuration);
                Configure(options, settings, settings.UserAudiences, environment);
                options.TokenValidationParameters.RoleClaimType = "groups";
            });
        services.AddOptions<JwtBearerOptions>(BookingJwtSettings.InternalScheme)
            .Configure<IConfiguration, IHostEnvironment>((options, configuration, environment) =>
            {
                var settings = BookingJwtSettings.Load(configuration);
                Configure(options, settings, settings.InternalAudiences, environment);
            });

        services.AddSingleton<IStartupFilter, JwtSettingsStartupFilter>();
        return services;
    }

    private static void Configure(
        JwtBearerOptions options, BookingJwtSettings settings, IReadOnlyList<string> audiences, IHostEnvironment environment)
    {
        options.Authority = settings.Authority;
        options.RequireHttpsMetadata = !environment.IsDevelopment();

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = settings.Authority,
            ValidateAudience = true,
            ValidAudiences = audiences,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidTypes = new[] { BookingJwtSettings.AccessTokenType }
        };

        if (environment.IsDevelopment())
        {
            options.BackchannelHttpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
        }
    }

    private sealed class JwtSettingsStartupFilter(IConfiguration configuration) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            BookingJwtSettings.Load(configuration);
            return next;
        }
    }
}
