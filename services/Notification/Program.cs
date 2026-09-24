using System;
using System.Reflection;
using BuildingBlocks;
using DbUp;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Notification.Service.Db;
using Notification.Service.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
var logger = loggerFactory.CreateLogger("Notification.Migrations");

try
{
    var upgrader = DeployChanges.To
        .PostgresqlDatabase(connectionString)
        .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
        .LogToConsole()
        .Build();

    var result = upgrader.PerformUpgrade();
    if (!result.Successful)
    {
        logger.LogError(result.Error, "Notification database upgrade failed");
    }
}
catch (Exception ex)
{
    logger.LogWarning(ex, "Could not run automatic database migration at startup");
}

// Services DI
builder.Services.AddSingleton<DbConnectionFactory>();
builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddHttpClient<IEmailService, EmailJsService>();

// Kafka Event Listener
builder.Services.AddHostedService<TicketIssuedEventListener>();

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseRouting();
app.MapControllers();

app.Run();
