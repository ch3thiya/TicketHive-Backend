using BuildingBlocks;
using Xunit;

namespace BuildingBlocks.Tests;

public class InternalServiceTokenClientOptionsValidatorTests
{
    private readonly InternalServiceTokenClientOptionsValidator _validator = new();

    private static InternalServiceTokenClientOptions ValidOptions() => new()
    {
        TokenEndpoint = "https://identity.example/oauth2/token",
        ClientId = "inventory-client",
        ClientSecret = "secret",
        Scope = "inventory:write"
    };

    [Fact]
    public void AllCredentialsPresent_Succeeds()
    {
        Assert.True(_validator.Validate(null, ValidOptions()).Succeeded);
    }

    [Theory]
    [InlineData("TokenEndpoint")]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    [InlineData("Scope")]
    public void EachMissingCredential_FailsWithItsConfigurationKey(string property)
    {
        var options = ValidOptions();
        switch (property)
        {
            case "TokenEndpoint": options.TokenEndpoint = "  "; break;
            case "ClientId": options.ClientId = "  "; break;
            case "ClientSecret": options.ClientSecret = "  "; break;
            case "Scope": options.Scope = "  "; break;
        }

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains($"Wso2:InternalApi:{property}", result.FailureMessage);
    }

    [Fact]
    public void MultipleMissingCredentials_AreListedSeparately()
    {
        var options = ValidOptions();
        options.ClientId = "";
        options.Scope = "";

        var result = _validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("Wso2:InternalApi:ClientId, Wso2:InternalApi:Scope", result.FailureMessage);
    }
}
