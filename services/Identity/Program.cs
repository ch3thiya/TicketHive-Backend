using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using BuildingBlocks;
using Identity.Service.Clients;
using Identity.Service.Db;
using Identity.Service.Services;
// Load root .env file if available; a real environment variable already set
// (docker-compose, Container Apps) always wins over the .env file.
DotNetEnv.Env.TraversePath().NoClobber().Load();

if (args.Contains("--migrate"))
{
    var migrationBuilder = WebApplication.CreateBuilder(args);
    var migrationConnectionString = migrationBuilder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is missing from configuration.");
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
    var migrationLogger = loggerFactory.CreateLogger("Identity.Migrations");

    try
    {
        DatabaseMigrator.Migrate(migrationConnectionString, Assembly.GetExecutingAssembly(), migrationLogger);
        return 0;
    }
    catch (Exception ex)
    {
        migrationLogger.LogError(ex, "Identity database migration failed");
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10MB
});

// Register CORS to allow React Frontend requests
var allowedFrontendOrigins = builder.Configuration["Cors:AllowedOrigins"]?
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ?? new[]
    {
        "http://localhost:5173",  // Local dev
        "https://tickethive-frontend.victoriouscoast-e1f47869.southeastasia.azurecontainerapps.io"  // Azure production
    };

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(allowedFrontendOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

// Register DB Connection
builder.Services.AddSingleton<DbConnectionFactory>();
builder.Services.AddScoped<IAccountRepository, AccountRepository>();
builder.Services.AddScoped<IOrganizerSuspensionService, OrganizerSuspensionService>();

// The WSO2 admin credentials and M2M client credentials are required: a
// service that silently starts without them fails later with a confusing
// SCIM auth error instead of a clear startup message.
builder.Services.AddSingleton<IValidateOptions<Wso2AdminOptions>, Wso2AdminOptionsValidator>();
builder.Services.AddOptions<Wso2AdminOptions>()
    .Bind(builder.Configuration.GetSection(Wso2AdminOptions.SectionName))
    .ValidateOnStart();

// Register WSO2 SCIM 2.0 HttpClient with basic auth credentials
builder.Services.AddHttpClient<IWso2ScimClient, Wso2ScimClient>((sp, client) =>
{
    var wso2BaseUrl = builder.Configuration["Wso2:BaseUrl"] ?? "https://localhost:9443/";
    client.BaseAddress = new Uri(wso2BaseUrl);

    var adminOptions = sp.GetRequiredService<IOptions<Wso2AdminOptions>>().Value;
    var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{adminOptions.AdminUsername}:{adminOptions.AdminPassword}"));
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
})
.ConfigurePrimaryHttpMessageHandler(() =>
{
    var handler = new HttpClientHandler();
    // Bypasses SSL certificate check for local self-signed WSO2 certs in development
    if (builder.Environment.IsDevelopment())
    {
        handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
    }
    return handler;
});

var requiredInternalScope = builder.Configuration["Wso2:InternalApi:RequiredScope"]
    ?? throw new InvalidOperationException("Configuration 'Wso2:InternalApi:RequiredScope' is missing.");

// Configure JWT Bearer Authentication pointing to WSO2 Identity Server
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.Authority = builder.Configuration["Jwt:Authority"];
        options.Audience = builder.Configuration["Jwt:Audience"];
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Authority"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RoleClaimType = "groups" // Map Asgardeo's groups claim to the standard .NET Role
        };

        options.IncludeErrorDetails = true;
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("JwtAuth");
                logger.LogError(context.Exception, "JWT Authentication failed: {Message}", context.Exception.Message);
                return Task.CompletedTask;
            }
        };

        // Bypass SSL validation for JWKS key discovery during local development
        if (builder.Environment.IsDevelopment())
        {
            options.BackchannelHttpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
        }
    })
    .AddJwtBearer("Internal", options =>
    {
        // Asgardeo issues client-credentials tokens with `aud` set to the
        // calling application's own client ID, not an API resource
        // identifier, so audience validation is deliberately off here.
        // Authorization instead comes from the `scope` claim, enforced by
        // the InternalService policy below. Mirrors Catalog and Inventory.
        options.Authority = builder.Configuration["Jwt:Authority"];
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Authority"],
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
        };

        options.Events = new JwtBearerEvents
        {
            // Asgardeo carries scopes as one space-separated `scope` claim;
            // split it so RequireClaim can match a single scope value.
            OnTokenValidated = context =>
            {
                if (context.Principal?.Identity is ClaimsIdentity identity)
                {
                    var scopeClaim = context.Principal.FindFirst("scope")?.Value;
                    if (!string.IsNullOrWhiteSpace(scopeClaim))
                    {
                        foreach (var scope in scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            identity.AddClaim(new Claim("scope", scope));
                        }
                    }
                }

                return Task.CompletedTask;
            }
        };

        // Bypass SSL validation for JWKS key discovery during local development
        if (builder.Environment.IsDevelopment())
        {
            options.BackchannelHttpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
        }
    });

builder.Services.AddAuthorization(options =>
{
    // The `aut` claim is APPLICATION for Asgardeo service tokens; requiring it
    // alongside the scope narrows this policy to machine-to-machine callers.
    options.AddPolicy("InternalService", policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            policy.RequireAssertion(_ => true);
        }
        else
        {
            policy.AddAuthenticationSchemes("Internal")
                .RequireClaim("scope", requiredInternalScope)
                .RequireClaim("aut", "APPLICATION");
        }
    });
});

var app = builder.Build();
app.UseServiceDefaults();

// Development only: migrate the database at startup before the host starts.
DatabaseMigrator.MigrateIfDevelopment(app.Environment, app.Configuration, connectionString =>
    DatabaseMigrator.Migrate(connectionString, Assembly.GetExecutingAssembly(), app.Logger));

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("AllowFrontend");

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.MapDefaultEndpoints();

app.MapControllers();

app.MapGet("/", () => Results.Ok(new { service = "Identity Service", status = "Healthy" }));

app.Run();
return 0;

public partial class Program { }
