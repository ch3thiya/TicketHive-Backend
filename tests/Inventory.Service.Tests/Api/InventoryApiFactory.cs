using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Inventory.Service.Clients;
using Inventory.Service.Db;
using Inventory.Service.Models;

namespace Inventory.Service.Tests.Api;

/// <summary>
/// Boots the real Inventory pipeline (routing, the InternalService policy)
/// with the stock repository stubbed out and real JWT validation replaced
/// by test handlers, so these tests exercise authentication/authorization
/// and status codes only; SQL behaviour is covered by the repository's own
/// Testcontainers integration tests. Uses the "Testing" environment so
/// startup never touches a real database.
///
/// The customer-facing scheme is replaced by overriding the default
/// authenticate/challenge scheme, the same way CatalogApiFactory does,
/// since AvailabilityController's [AllowAnonymous] endpoint still resolves
/// the default scheme when a token is present. The "Internal" scheme is
/// pinned by name in the InternalService policy
/// (AddAuthenticationSchemes("Internal")), so it can't be swapped that way;
/// its real JwtBearer registration is removed from AuthenticationOptions
/// first, then a test handler is registered under the same scheme name.
/// </summary>
public class InventoryApiFactory : WebApplicationFactory<Program>
{
    public Mock<IStockRepository> StockRepositoryMock { get; } = new();
    public FakeSalesEligibilityClient SalesEligibility { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdmissionToken:PublicKeyPem"] = AdmissionTokenTestKeys.PublicKeyPem
            });
        });

        builder.ConfigureServices(services =>
        {
            // Authorization/authentication tests only care about status
            // codes, not SQL behaviour (that is covered by the repository's
            // own Testcontainers integration tests), so the repository is
            // stubbed out to keep these tests off a real database.
            services.RemoveAll<ISalesEligibilityClient>();
            services.AddSingleton<ISalesEligibilityClient>(SalesEligibility);
            services.RemoveAll<IStockRepository>();
            services.TryAddSingleton(StockRepositoryMock.Object);

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

    public void ResetRepositoryDefaults()
    {
        StockRepositoryMock.Reset();
        StockRepositoryMock
            .Setup(r => r.InitializeAsync(It.IsAny<ShowRules>(), It.IsAny<List<StockItem>>()))
            .ReturnsAsync((ShowRules rules, List<StockItem> categories) => categories
                .Select(c => new StockItem
                {
                    ShowId = c.ShowId,
                    CategoryId = c.CategoryId,
                    Capacity = c.Capacity,
                    Available = c.Capacity,
                    UnitPrice = c.UnitPrice,
                    Currency = c.Currency,
                    AllocationMode = c.AllocationMode
                })
                .ToList());
        StockRepositoryMock
            .Setup(r => r.GetByShowIdAsync(It.IsAny<System.Guid>()))
            .ReturnsAsync(new List<StockItem>());
    }
}
