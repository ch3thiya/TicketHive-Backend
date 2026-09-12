using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Identity.Service.Clients;
using Identity.Service.Db;

namespace Identity.Service.Tests.Api;

/// <summary>
/// Boots the real Identity pipeline (routing, the Admin-role authorization
/// requirement on <c>AdminController</c>) with the repository and SCIM
/// client stubbed out, and real JWT validation replaced by
/// <see cref="TestAuthHandler"/>. These tests exercise authorization and
/// status codes only; SQL behaviour is covered by the repository's own
/// Testcontainers integration tests. Uses the "Testing" environment so
/// startup never touches a real database.
/// </summary>
public class AdminApiFactory : WebApplicationFactory<Program>
{
    public Mock<IAccountRepository> AccountRepositoryMock { get; } = new();
    public Mock<IWso2ScimClient> ScimClientMock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAccountRepository>();
            services.TryAddSingleton(AccountRepositoryMock.Object);

            services.RemoveAll<IWso2ScimClient>();
            services.TryAddSingleton(ScimClientMock.Object);

            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, options => { });

            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                options.DefaultScheme = TestAuthHandler.SchemeName;
            });
        });
    }
}
