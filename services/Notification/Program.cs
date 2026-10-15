using Microsoft.AspNetCore.Authentication.JwtBearer;
using System;
using System.Reflection;
using BuildingBlocks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Notification.Service.Clients;
using Notification.Service.Db;
using Notification.Service.Services;
// Load root .env file if available; a real environment variable already set
// (docker-compose, Container Apps) always wins over the .env file.
DotNetEnv.Env.TraversePath().NoClobber().Load();

if (args.Contains("--migrate"))
{
    var migrationBuilder = WebApplication.CreateBuilder(args);
    var migrationConnectionString = migrationBuilder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is missing from configuration.");
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
    var migrationLogger = loggerFactory.CreateLogger("Notification.Migrations");

    try
    {
        DatabaseMigrator.Migrate(migrationConnectionString, Assembly.GetExecutingAssembly(), migrationLogger);
        return 0;
    }
    catch (Exception ex)
    {
        migrationLogger.LogError(ex, "Notification database migration failed");
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Services DI
builder.Services.AddSingleton<DbConnectionFactory>();
builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddHttpClient<IEmailService, EmailJsService>();

// Kafka Event Listener
builder.Services.AddHostedService<TicketIssuedEventListener>();

builder.Services.AddControllers();
builder.Services.AddTicketHiveOpenApi();

builder.Services.AddScoped<CancellationEmailRepository>();
builder.Services.Configure<CancellationEmailOptions>(options =>
{
    var emailJs = builder.Configuration.GetSection("EmailJS");
    options.ServiceId = emailJs["ServiceId"] ?? "";
    options.TemplateId = emailJs["TemplateId"] ?? "";
    options.PublicKey = emailJs["PublicKey"] ?? "";
    options.PrivateKey = emailJs["PrivateKey"] ?? "";
    options.Simulate = false;

    builder.Configuration.GetSection("CancellationEmail").Bind(options);
});
builder.Services.AddHttpClient<CancellationEmailSender>();
builder.Services.AddHostedService<CancellationEmailWorker>();
// Event reminders (SCRUM-172): one row per order, a timed job, and read-only calls to Catalog and Booking.
builder.Services.AddScoped<EventReminderRepository>();
builder.Services.Configure<ReminderOptions>(builder.Configuration.GetSection(ReminderOptions.SectionName));
builder.Services.AddSingleton<ReminderMetrics>();
builder.Services.AddInternalServiceTokenClient(builder.Configuration);
builder.Services.AddHttpClient<IReminderCatalogClient, ReminderCatalogClient>(client =>
    client.BaseAddress = new Uri(builder.Configuration["CatalogService:BaseUrl"] ?? "http://catalog:8080/"))
    .AddHttpMessageHandler<InternalServiceAuthenticationHandler>();
builder.Services.AddHttpClient<IReminderBookingClient, ReminderBookingClient>(client =>
    client.BaseAddress = new Uri(builder.Configuration["BookingService:BaseUrl"] ?? "http://booking:8080/"))
    .AddHttpMessageHandler<InternalServiceAuthenticationHandler>();
builder.Services.AddHttpClient<IReminderEmailSender, ReminderEmailSender>();
builder.Services.AddScoped<ReminderSeeder>();
builder.Services.AddScoped<ReminderProcessor>();
builder.Services.AddHostedService<ReminderWorker>();
builder.Services.AddAuthentication("Bearer").AddJwtBearer("Bearer", options =>
{
    options.Authority = builder.Configuration["Jwt:Authority"];
    options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = builder.Configuration["Jwt:Authority"],
        ValidateAudience = false,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true
    };

    if (builder.Environment.IsDevelopment())
    {
        options.BackchannelHttpHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
    }
});
builder.Services.AddCancellationInternalAuthorization("notification:write");
var app = builder.Build();
app.UseServiceDefaults();

// Development only: migrate the database at startup before the host starts.
DatabaseMigrator.MigrateIfDevelopment(app.Environment, app.Configuration, connectionString =>
    DatabaseMigrator.Migrate(connectionString, Assembly.GetExecutingAssembly(), app.Logger));

app.MapOpenApi().AllowAnonymous();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapDefaultEndpoints();
app.MapControllers();

app.Run();
return 0;
