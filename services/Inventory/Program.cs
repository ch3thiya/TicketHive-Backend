using System.Reflection;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using BuildingBlocks;
using Inventory.Service.Db;
using Inventory.Service.Services;

if (args.Contains("--migrate"))
{
    var migrationBuilder = WebApplication.CreateBuilder(args);
    var migrationConnectionString = migrationBuilder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is missing from configuration.");
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
    var migrationLogger = loggerFactory.CreateLogger("Inventory.Migrations");

    try
    {
        DatabaseMigrator.Migrate(migrationConnectionString, Assembly.GetExecutingAssembly(), migrationLogger);
        return 0;
    }
    catch (Exception ex)
    {
        migrationLogger.LogError(ex, "Inventory database migration failed");
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddSingleton<DbConnectionFactory>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IStockRepository, StockRepository>();
builder.Services.AddScoped<IStockService, StockService>();
builder.Services.AddScoped<IAllocationStrategy, GeneralAdmissionAllocationStrategy>();
builder.Services.AddScoped<IHoldRepository, HoldRepository>();
builder.Services.AddScoped<IHoldService, HoldService>();
builder.Services.AddHostedService<ExpiredHoldReleaseWorker>();

var requiredInternalScope = builder.Configuration["Wso2:InternalApi:RequiredScope"]
    ?? throw new InvalidOperationException("Configuration 'Wso2:InternalApi:RequiredScope' is missing.");

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

// Customer-facing tokens (default scheme), mirroring Catalog's JWT bearer
// configuration exactly.
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

        // Deliberately no DangerousAcceptAnyServerCertificateValidator here:
        // Asgardeo is a public endpoint with a valid certificate, so the
        // Catalog-style local-identity-server bypass does not apply.
    })
    .AddJwtBearer("Internal", options =>
    {
        // Asgardeo issues client-credentials tokens with `aud` set to the
        // calling application's own client ID, not an API resource
        // identifier (confirmed against a real token), so audience
        // validation is deliberately off here. Authorization instead comes
        // from the `scope` claim, enforced by the InternalService policy
        // below.
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
            // split it into individual claims so RequireClaim can match a
            // single scope value.
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
    });

builder.Services.AddAuthorization(options =>
{
    // The `aut` claim is APPLICATION for Asgardeo service tokens; requiring
    // it alongside the scope narrows this policy to machine-to-machine
    // callers even if a customer token ever carried a matching scope.
    options.AddPolicy("InternalService", policy => policy
        .AddAuthenticationSchemes("Internal")
        .RequireClaim("scope", requiredInternalScope)
        .RequireClaim("aut", "APPLICATION"));
});

// Per-user limit on POST /api/inventory/holds (ADR-008): 15 attempts per 10
// seconds. A real customer places one hold and maybe retries after a
// network hiccup — nowhere near this; a script racing an on-sale hits it
// within its first burst. Partitioned by the caller's sub so one customer's
// limit never affects another's, and falls back to the remote IP for a
// request that somehow reaches the handler unauthenticated.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("HoldCreation", httpContext =>
    {
        var partitionKey = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? httpContext.User.FindFirst("sub")?.Value
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 15,
            Window = TimeSpan.FromSeconds(10),
            QueueLimit = 0
        });
    });

    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return ValueTask.CompletedTask;
    };
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
app.UseRateLimiter();
app.MapDefaultEndpoints();

app.MapControllers();

app.Run();
return 0;

public partial class Program { }
