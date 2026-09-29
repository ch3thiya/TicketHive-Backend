using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using BuildingBlocks;
using Gateway.Proxy;
using Yarp.ReverseProxy.Transforms;

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
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(context => context.AddResponseTransform(UpstreamCorsHeaderRemover.RemoveAsync));

// CORS is answered here for every path, including preflight requests,
// which the middleware completes before authentication runs.
const string FrontendCorsPolicy = "frontend";
var allowedOrigins = builder.Configuration["Cors:AllowedOrigins"]?
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ?? [];

builder.Services.AddCors(options =>
{
    options.AddPolicy(FrontendCorsPolicy, policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Container Apps ingress is the only hop in front of the gateway, so trust
// exactly one X-Forwarded-For entry from it.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.ForwardLimit = 1;
});

builder.Services.AddOptions<ClientRateLimitOptions>()
    .Bind(builder.Configuration.GetSection(ClientRateLimitOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddRateLimiter(_ => { });
builder.Services.AddOptions<RateLimiterOptions>()
    .Configure<IOptions<ClientRateLimitOptions>>((options, clientLimits) =>
    {
        options.GlobalLimiter = ClientRateLimiter.Create(clientLimits.Value);
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, cancellationToken) =>
        {
            var httpContext = context.HttpContext;
            httpContext.RequestServices.GetRequiredService<ILogger<Program>>()
                .LogWarning("Rate limit rejected {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);

            await httpContext.RequestServices.GetRequiredService<IProblemDetailsService>()
                .TryWriteAsync(new ProblemDetailsContext
                {
                    HttpContext = httpContext,
                    ProblemDetails = { Status = StatusCodes.Status429TooManyRequests, Title = "Too many requests" }
                });
        };
    });

var app = builder.Build();
app.UseForwardedHeaders();
app.UseServiceDefaults();
app.UseRouting();

app.UseCors(FrontendCorsPolicy);
app.UseRateLimiter();
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
