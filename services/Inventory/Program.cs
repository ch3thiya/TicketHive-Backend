using System.Reflection;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using BuildingBlocks;
using Inventory.Service.Db;
using Inventory.Service.Models;
using Inventory.Service.Services;
using Inventory.Service.Clients;

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
builder.Services.AddInternalServiceTokenClient(builder.Configuration);
// Sales eligibility comes from Catalog over an authenticated service call (resilience defaults
// apply). Needs catalog:read in Wso2:InternalApi:Scope.
builder.Services.AddHttpClient<ISalesEligibilityClient, SalesEligibilityClient>(client =>
    client.BaseAddress = new Uri(builder.Configuration["CatalogService:BaseUrl"] ?? "http://catalog:8080/"))
    .AddHttpMessageHandler<InternalServiceAuthenticationHandler>();
builder.Services.Configure<HoldExpirySweepOptions>(builder.Configuration.GetSection(HoldExpirySweepOptions.SectionName));
builder.Services.AddSingleton<HoldExpiryMetrics>();
builder.Services.AddHostedService<ExpiredHoldReleaseWorker>();

var requiredInternalScope = builder.Configuration["Wso2:InternalApi:RequiredScope"]
    ?? throw new InvalidOperationException("Configuration 'Wso2:InternalApi:RequiredScope' is missing.");

// The admission-token gate must fail closed: a missing public key stops
// Inventory from starting rather than falling back to no verification
// (that fallback is exactly the bug this replaces — see HoldService).
// ValidateOnStart, not a plain throw, so the check runs against the fully
// assembled configuration (including what a WebApplicationFactory test
// host layers on) instead of the snapshot available before builder.Build().
builder.Services.AddSingleton<IValidateOptions<AdmissionTokenOptions>, AdmissionTokenOptionsValidator>();
builder.Services.AddOptions<AdmissionTokenOptions>()
    .Bind(builder.Configuration.GetSection(AdmissionTokenOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddSingleton<IAdmissionTokenVerifier>(sp =>
{
    var options = sp.GetRequiredService<IOptions<AdmissionTokenOptions>>().Value;
    return new AdmissionTokenVerifier(options.PublicKeyPem, options.Issuer, sp.GetRequiredService<TimeProvider>());
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
            ValidateAudience = false,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RoleClaimType = "groups" // Map Asgardeo's groups claim to the standard .NET Role
        };

        // Bypass SSL validation for JWKS key discovery during local development if needed
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

builder.Services.AddScoped<CancellationRepository>();
builder.Services.AddCancellationInternalAuthorization("inventory:write");
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
