using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Inventory.Service.Tests.Api;

namespace Inventory.Service.Tests.Integration;

/// <summary>
/// Boots the real Inventory pipeline — real controllers, real repositories,
/// real SQL — against a real Postgres Testcontainer, so the holds
/// concurrency tests exercise the actual conditional-UPDATE concurrency
/// control end to end (ADR-007), not a mocked repository. Authentication is
/// swapped exactly the way InventoryApiFactory does it: the customer-facing
/// scheme becomes a test handler driven by a header, and the real
/// "Internal" JwtBearer registration is removed first so a test handler can
/// take its name (holds endpoints don't use it, but Program.cs registers it
/// unconditionally).
/// </summary>
public class HoldsApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public HoldsApiFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _connectionString,
                ["AdmissionToken:PublicKeyPem"] = AdmissionTokenTestKeys.PublicKeyPem
            });
        });

        builder.ConfigureServices(services =>
        {
            services.Configure<AuthenticationOptions>(options =>
            {
                if (options.Schemes is IList<AuthenticationSchemeBuilder> schemes)
                {
                    foreach (var stale in schemes.Where(s => s.Name == InternalTestAuthHandler.SchemeName).ToList())
                    {
                        schemes.Remove(stale);
                    }
                }

                options.SchemeMap.Remove(InternalTestAuthHandler.SchemeName);
            });

            services.AddAuthentication(CustomerTestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, CustomerTestAuthHandler>(CustomerTestAuthHandler.SchemeName, options => { })
                .AddScheme<AuthenticationSchemeOptions, InternalTestAuthHandler>(InternalTestAuthHandler.SchemeName, options => { });

            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = CustomerTestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = CustomerTestAuthHandler.SchemeName;
                options.DefaultScheme = CustomerTestAuthHandler.SchemeName;
            });
        });
    }
}
