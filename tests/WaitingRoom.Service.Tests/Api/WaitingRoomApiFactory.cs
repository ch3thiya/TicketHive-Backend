using System.Collections.Generic;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace WaitingRoom.Service.Tests.Api;

/// <summary>
/// Boots the real WaitingRoom pipeline — the real Program.cs registrations,
/// the real QueueRepository and QueueService, no mocks — against a real
/// Postgres (the caller's Testcontainers connection string). Only the
/// connection string, the admission-token key (required at startup by
/// AdmissionTokenOptionsValidator) and JWT validation are overridden, so a
/// discrepancy between a hand-built unit test and the actually-wired
/// application would show up here.
/// </summary>
public class WaitingRoomApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public WaitingRoomApiFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            using var rsa = RSA.Create(2048);
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _connectionString,
                ["AdmissionToken:PrivateKeyPem"] = rsa.ExportPkcs8PrivateKeyPem(),
                // The real QueueAdmissionScheduler runs as a real hosted
                // service in this factory; a short period lets a scheduler
                // test observe a real tick without waiting a full poll.
                ["QueueAdmissionScheduler:TickIntervalSeconds"] = "1"
            });
        });

        builder.ConfigureServices(services =>
        {
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
