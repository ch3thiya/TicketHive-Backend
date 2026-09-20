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
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<IPayHereService, PayHereService>();
builder.Services.AddSingleton<IKafkaProducer, KafkaProducer>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddHostedService<PaymentEventListener>();

var inventoryBaseUrl = builder.Configuration["InventoryService:BaseUrl"] ?? "http://localhost:5219/";
builder.Services.AddHttpClient<IInventoryClient, InventoryClient>(client =>
{
    client.BaseAddress = new Uri(inventoryBaseUrl);
});

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

var app = builder.Build();
app.UseServiceDefaults();

DatabaseMigrator.MigrateIfDevelopment(app.Environment, app.Configuration, connectionString =>
    DatabaseMigrator.Migrate(connectionString, Assembly.GetExecutingAssembly(), app.Logger));

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

app.Run();
return 0;

public partial class Program { }
