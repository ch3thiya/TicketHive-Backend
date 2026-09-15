using System.Reflection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using BuildingBlocks;
using Catalog.Service.Authorization;
using Catalog.Service.Clients;
using Catalog.Service.Db;
using Catalog.Service.Services;
// Load root .env file if available; a real environment variable already set
// (docker-compose, Container Apps) always wins over the .env file.
DotNetEnv.Env.TraversePath().NoClobber().Load();

if (args.Contains("--migrate"))
{
    var migrationBuilder = WebApplication.CreateBuilder(args);
    var migrationConnectionString = migrationBuilder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is missing from configuration.");
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
    var migrationLogger = loggerFactory.CreateLogger("Catalog.Migrations");

    try
    {
        DatabaseMigrator.Migrate(migrationConnectionString, Assembly.GetExecutingAssembly(), migrationLogger);
        return 0;
    }
    catch (Exception ex)
    {
        migrationLogger.LogError(ex, "Catalog database migration failed");
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

// Register DB Connection, Repositories and Services
builder.Services.AddSingleton<DbConnectionFactory>();
builder.Services.AddScoped<IEventRepository, EventRepository>();
builder.Services.AddScoped<IEventService, EventService>();
builder.Services.AddScoped<IVenueRepository, VenueRepository>();
builder.Services.AddScoped<IVenueService, VenueService>();

// Register the Identity organizer-status client, cached briefly so suspension
// takes effect quickly without a call on every request.
builder.Services.AddMemoryCache();
builder.Services.Configure<OrganizerStatusClientOptions>(builder.Configuration.GetSection(OrganizerStatusClientOptions.SectionName));
builder.Services.AddHttpClient<IOrganizerStatusClient, OrganizerStatusClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<OrganizerStatusClientOptions>>().Value;
    if (!string.IsNullOrWhiteSpace(options.BaseUrl))
    {
        client.BaseAddress = new Uri(options.BaseUrl);
    }
});

// Register the internal-token client (client-credentials M2M token, cached)
// and the Inventory client that attaches it to outgoing calls. The standard
// resilience handler already applies to every typed client via
// AddServiceDefaults()'s ConfigureHttpClientDefaults.
builder.Services.AddInternalServiceTokenClient(builder.Configuration);
builder.Services.Configure<InventoryClientOptions>(builder.Configuration.GetSection(InventoryClientOptions.SectionName));
builder.Services.AddHttpClient<IInventoryClient, InventoryClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<InventoryClientOptions>>().Value;
    if (!string.IsNullOrWhiteSpace(options.BaseUrl))
    {
        client.BaseAddress = new Uri(options.BaseUrl);
    }
})
.AddHttpMessageHandler<InternalServiceAuthenticationHandler>();


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

// Configure JWT Bearer Authentication pointing to WSO2 Identity Server
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
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

        // Bypass SSL validation for JWKS key discovery during local development
        if (builder.Environment.IsDevelopment())
        {
            options.BackchannelHttpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
        }
    });

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuthorizationHandler, ActiveOrganizerAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, OrganizerAuthorizationResultHandler>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ActiveOrganizer", policy => policy.Requirements.Add(new ActiveOrganizerRequirement()));
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

app.MapGet("/", () => Results.Ok(new { service = "Catalog Service", status = "Healthy" }));

app.Run();
return 0;

public partial class Program { }
