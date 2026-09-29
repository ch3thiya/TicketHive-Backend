using BuildingBlocks;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Routes and clusters live in the "ReverseProxy" config section so Azure can
// override each destination with ReverseProxy__Clusters__<name>__Destinations__primary__Address.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();
app.UseServiceDefaults();

app.MapDefaultEndpoints();

// Internal service-to-service routes are never reachable through the edge,
// even if a proxy route is misconfigured later.
app.Map("/internal/{**rest}", (HttpContext context, ILogger<Program> logger) =>
{
    logger.LogWarning("Blocked request to internal path {Path}", context.Request.Path);
    return Results.Problem(statusCode: StatusCodes.Status404NotFound);
});

app.MapReverseProxy();

app.MapFallback(() => Results.Problem(statusCode: StatusCodes.Status404NotFound));

app.Run();

public partial class Program { }
