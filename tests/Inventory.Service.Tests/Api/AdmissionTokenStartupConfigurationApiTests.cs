using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Inventory.Service.Tests.Api;

/// <summary>
/// End-to-end proof that the admission-token public key check
/// (AdmissionTokenOptionsValidator, wired via .ValidateOnStart() in
/// Program.cs) actually stops the host from starting when the key is
/// missing, not just that the validator is correct in isolation. A gate
/// that silently starts without a working key is worse than no gate (AC10).
/// </summary>
public class AdmissionTokenStartupConfigurationApiTests
{
    [Fact]
    public void Startup_MissingAdmissionTokenPublicKey_ThrowsNamingMissingKey()
    {
        // Arrange
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");

            // appsettings.json has no AdmissionToken:PublicKeyPem key today
            // (it comes from deployment config, never checked in), but null
            // it explicitly so this test stays correct even if a default is
            // ever added there.
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AdmissionToken:PublicKeyPem"] = null
                });
            });
        });

        // Act & Assert
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("AdmissionToken:PublicKeyPem", ex.Message);
    }
}
