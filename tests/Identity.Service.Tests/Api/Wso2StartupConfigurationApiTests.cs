using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Identity.Service.Tests.Api;

/// <summary>
/// End-to-end proof that the WSO2 admin/M2M configuration check
/// (Wso2AdminOptionsValidator, wired via .ValidateOnStart() in Program.cs)
/// actually stops the host from starting, not just that the validator is
/// correct in isolation (see Wso2AdminOptionsValidatorTests).
/// </summary>
public class Wso2StartupConfigurationApiTests
{
    [Fact]
    public void Startup_MissingWso2AdminConfiguration_ThrowsNamingMissingKeys()
    {
        // Arrange
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");

            // appsettings.json has no Wso2:Admin*/M2m* keys today, but null
            // them explicitly so this test stays correct even if defaults
            // are ever added there.
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Wso2:AdminUsername"] = null,
                    ["Wso2:AdminPassword"] = null,
                    ["Wso2:M2mClientId"] = null,
                    ["Wso2:M2mClientSecret"] = null,
                });
            });
        });

        // Act & Assert
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Wso2:AdminUsername", ex.Message);
        Assert.Contains("Wso2:AdminPassword", ex.Message);
        Assert.Contains("Wso2:M2mClientId", ex.Message);
        Assert.Contains("Wso2:M2mClientSecret", ex.Message);
    }
}
