using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Service.Tests.Api;

/// <summary>
/// Boots the real Inventory pipeline (routing, the InternalService policy)
/// with real JWT validation replaced by test handlers, so these tests
/// exercise authentication/authorization and status codes only. Uses the
/// "Testing" environment so startup never touches a real database.
///
/// The customer-facing scheme is replaced by overriding the default
/// authenticate/challenge scheme, the same way CatalogApiFactory does,
/// since PingController's [Authorize] uses whichever scheme is default.
/// The "Internal" scheme is pinned by name in the InternalService policy
/// (AddAuthenticationSchemes("Internal")), so it can't be swapped that way;
/// its real JwtBearer registration is removed from AuthenticationOptions
/// first, then a test handler is registered under the same scheme name.
/// </summary>
public class InventoryApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Remove Program.cs's real "Internal" JwtBearer scheme so a test
            // handler can be registered under the same name below.
            // AddScheme's own duplicate check only looks at SchemeMap, but
            // AuthenticationSchemeProvider rebuilds its lookup from the
            // Schemes list too, so both need the old entry removed.
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
