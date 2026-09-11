using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using BuildingBlocks;
using Identity.Service.Clients;
using Identity.Service.Db;
// Load root .env file if available
DotNetEnv.Env.TraversePath().Load();

// Load root .env file if available
DotNetEnv.Env.TraversePath().Load();

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

// Register DB Connection and Initializer
builder.Services.AddSingleton<DbConnectionFactory>();
builder.Services.AddScoped<DbInitializer>();
builder.Services.AddScoped<IAccountRepository, AccountRepository>();

// Register WSO2 SCIM 2.0 HttpClient with basic auth credentials
builder.Services.AddHttpClient<IWso2ScimClient, Wso2ScimClient>(client =>
{
    var wso2BaseUrl = builder.Configuration["Wso2:BaseUrl"] ?? "https://localhost:9443/";
    client.BaseAddress = new Uri(wso2BaseUrl);
    
    var username = builder.Configuration["Wso2:AdminUsername"] ?? "admin";
    var password = builder.Configuration["Wso2:AdminPassword"] ?? "admin";
    var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
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
    });

builder.Services.AddAuthorization();

var app = builder.Build();
app.UseServiceDefaults();

// Run Database Schema Initialization on Startup
using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<DbInitializer>();
    try
    {
        await initializer.InitializeAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to initialize the database schema on startup.");
    }
}

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
app.MapGet("/api/identity/init-db", async (DbInitializer initializer) =>
{
    await initializer.InitializeAsync();
    return Results.Ok(new { message = "Identity database schema initialized successfully." });
});

app.Run();

public partial class Program { }
