using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gateway.Tests;

// Every cluster points at a closed loopback port, so a forwarded request
// fails fast with 502 instead of reaching a real service. The JWT authority
// is an unresolvable host; no test sends a token, so metadata is never fetched.
public class GatewayFactory : WebApplicationFactory<Program>
{
    public const string AllowedOrigin = "http://localhost:5173";

    private const string UnreachableAddress = "http://127.0.0.1:1";

    private static readonly string[] Clusters =
        ["identity", "catalog", "inventory", "waitingroom", "booking", "payment"];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Jwt:Authority", "https://issuer.invalid/oauth2/token");
        builder.UseSetting("Jwt:Audience", "gateway-tests");
        builder.UseSetting("Cors:AllowedOrigins", AllowedOrigin);

        foreach (var cluster in Clusters)
        {
            builder.UseSetting(
                $"ReverseProxy:Clusters:{cluster}:Destinations:primary:Address",
                UnreachableAddress);
        }
    }
}
