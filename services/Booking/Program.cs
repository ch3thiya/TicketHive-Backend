using System.Reflection;
using BuildingBlocks;
using Booking.Service.Clients;
using Booking.Service.Db;
using Booking.Service.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

if (args.Contains("--migrate"))
{
    var migrationBuilder = WebApplication.CreateBuilder(args);
    var migrationConnectionString = migrationBuilder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is missing from configuration.");
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
    var migrationLogger = loggerFactory.CreateLogger("Booking.Migrations");

    try
    {
        DatabaseMigrator.Migrate(migrationConnectionString, Assembly.GetExecutingAssembly(), migrationLogger);
        return 0;
    }
    catch (Exception ex)
    {
        migrationLogger.LogError(ex, "Booking database migration failed");
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddSingleton<DbConnectionFactory>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITicketCodeGenerator, TicketCodeGenerator>();
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<ITicketRepository, TicketRepository>();
builder.Services.AddScoped<IPayHereService, PayHereService>();
builder.Services.AddSingleton<IKafkaProducer, KafkaProducer>();
builder.Services.AddScoped<ITicketService, TicketService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddHostedService<PaymentEventListener>();

// Inventory's internal hold endpoints require a client-credentials token with
// inventory:write, so the Inventory client attaches one on every call.
builder.Services.AddInternalServiceTokenClient(builder.Configuration);
var inventoryBaseUrl = builder.Configuration["InventoryService:BaseUrl"] ?? "http://localhost:5219/";
builder.Services.AddHttpClient<IInventoryClient, InventoryClient>(client =>
{
    client.BaseAddress = new Uri(inventoryBaseUrl);
})
.AddHttpMessageHandler<InternalServiceAuthenticationHandler>();

// Ticket validation asks Catalog whether the caller owns the show (needs catalog:read in
// Wso2:InternalApi:Scope). Shares CatalogService:BaseUrl with the cancellation clients.
var catalogBaseUrl = builder.Configuration["CatalogService:BaseUrl"] ?? "http://catalog:8080/";
builder.Services.AddHttpClient<IEntryAccessClient, EntryAccessClient>(client =>
{
    client.BaseAddress = new Uri(catalogBaseUrl);
})
.AddHttpMessageHandler<InternalServiceAuthenticationHandler>();

var allowedFrontendOrigins = builder.Configuration["Cors:AllowedOrigins"]?
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ?? new[]
    {
        "http://localhost:5173",
        "https://tickethive-frontend.victoriouscoast-e1f47869.southeastasia.azurecontainerapps.io"
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
            ValidateAudience = false,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RoleClaimType = "groups"
        };

        if (builder.Environment.IsDevelopment())
        {
            options.BackchannelHttpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
        }
    });

builder.Services.AddAuthorization();

builder.Services.AddScoped<CancellationRepository>();
builder.Services.AddScoped<CancellationClient>();
builder.Services.AddHostedService<CancellationWorker>();
builder.Services.AddCancellationInternalAuthorization("booking:write");
foreach (var service in new[] { "Catalog", "Inventory", "Payment", "Notification" })
{
    var endpoint = builder.Configuration[$"{service}Service:BaseUrl"] ?? $"http://{service.ToLowerInvariant()}:8080/";
    builder.Services.AddHttpClient($"Cancellation{service}", client => client.BaseAddress = new Uri(endpoint))
        .AddHttpMessageHandler<InternalServiceAuthenticationHandler>();
}
var app = builder.Build();
app.UseServiceDefaults();

DatabaseMigrator.MigrateIfDevelopment(app.Environment, app.Configuration, connectionString =>
    DatabaseMigrator.Migrate(connectionString, Assembly.GetExecutingAssembly(), app.Logger));

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("AllowFrontend");
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseAuthentication();
app.UseAuthorization();
app.MapDefaultEndpoints();
app.MapControllers();

app.Run();
return 0;

public partial class Program { }
