using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;
using Catalog.Service.Clients;
using Catalog.Service.Db;
using Catalog.Service.Models;

namespace Catalog.Service.Tests.Api;

/// <summary>
/// Boots the real Catalog pipeline (routing, the ActiveOrganizer policy, the
/// custom result handler) with the Identity HTTP call and the event
/// repository stubbed out, and real JWT validation replaced by
/// <see cref="TestAuthHandler"/>. These tests exercise authorization and
/// status codes only; SQL behaviour is covered by the repository's own
/// Testcontainers integration tests. Uses the "Testing" environment so
/// startup never touches a real database.
/// </summary>
public class CatalogApiFactory : WebApplicationFactory<Program>
{
    public Mock<IOrganizerStatusClient> OrganizerStatusClientMock { get; } = new();
    public Mock<IEventRepository> EventRepositoryMock { get; } = new();
    public Mock<IVenueRepository> VenueRepositoryMock { get; } = new();
    public Mock<IInventoryClient> InventoryClientMock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders().AddConsole());

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<CancellationClient>();
            services.TryAddSingleton(new CancellationClient(new CancellationTestHttpClientFactory()));
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
            services.RemoveAll<IOrganizerStatusClient>();
            services.TryAddSingleton(OrganizerStatusClientMock.Object);

            services.RemoveAll<IEventRepository>();
            services.TryAddSingleton(EventRepositoryMock.Object);

            services.RemoveAll<IVenueRepository>();
            services.TryAddSingleton(VenueRepositoryMock.Object);

            services.RemoveAll<IInventoryClient>();
            services.TryAddSingleton(InventoryClientMock.Object);

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

    public void ResetRepositoryDefaults()
    {
        EventRepositoryMock.Reset();
        EventRepositoryMock.Setup(r => r.CreateEventAsync(It.IsAny<Event>())).ReturnsAsync((Event e) => e);
        EventRepositoryMock.Setup(r => r.GetAllPublishedEventsAsync()).ReturnsAsync(new List<Event>());
        EventRepositoryMock.Setup(r => r.GetEventsByOrganizerIdAsync(It.IsAny<Guid>())).ReturnsAsync(new List<Event>());
        EventRepositoryMock.Setup(r => r.GetShowsByEventIdAsync(It.IsAny<Guid>())).ReturnsAsync(new List<Show>());

        VenueRepositoryMock.Reset();
        VenueRepositoryMock.Setup(r => r.GetAllVenuesAsync()).ReturnsAsync(new List<Venue>());

        InventoryClientMock.Reset();
        InventoryClientMock
            .Setup(c => c.InitializeShowStockAsync(It.IsAny<Guid>(), It.IsAny<InitializeShowStockRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }
}

file sealed class CancellationTestHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new OkHandler()) { BaseAddress = new Uri("http://cancellation.test/") };
    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"orders\":{}}", System.Text.Encoding.UTF8, "application/json") });
    }
}
