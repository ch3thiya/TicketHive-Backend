using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using BuildingBlocks;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Mirrors the services' user-token validation exactly, so the edge never
// rejects a token a service would accept. The Authorization header is
// forwarded unchanged and services still apply their own role checks.
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
            RoleClaimType = "groups"
        };
    });

// Proxy routes opt into "authenticated" explicitly; the fallback policy
// covers any route added without one. Public routes use YARP's built-in
// "anonymous" policy.
builder.Services.AddAuthorization(options =>
{
    var authenticated = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    options.AddPolicy("authenticated", authenticated);
    options.FallbackPolicy = authenticated;
});

// Routes and clusters live in the "ReverseProxy" config section so Azure can
// override each destination with ReverseProxy__Clusters__<name>__Destinations__primary__Address.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();
app.UseServiceDefaults();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();

// Internal service-to-service routes are never reachable through the edge,
// even if a proxy route is misconfigured later.
app.Map("/internal/{**rest}", (HttpContext context, ILogger<Program> logger) =>
{
    logger.LogWarning("Blocked request to internal path {Path}", context.Request.Path);
    return Results.Problem(statusCode: StatusCodes.Status404NotFound);
}).AllowAnonymous();

app.MapReverseProxy();

app.MapFallback(() => Results.Problem(statusCode: StatusCodes.Status404NotFound))
    .AllowAnonymous();

app.Run();

public partial class Program { }
