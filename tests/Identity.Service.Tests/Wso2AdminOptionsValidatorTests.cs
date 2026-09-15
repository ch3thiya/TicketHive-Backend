using Xunit;
using Identity.Service.Clients;

namespace Identity.Service.Tests;

public class Wso2AdminOptionsValidatorTests
{
    private readonly Wso2AdminOptionsValidator _validator = new();

    private static Wso2AdminOptions ValidOptions() => new()
    {
        AdminUsername = "admin-user",
        AdminPassword = "admin-password",
        M2mClientId = "m2m-client-id",
        M2mClientSecret = "m2m-client-secret"
    };

    [Fact]
    public void Validate_AllValuesPresent_ReturnsSuccess()
    {
        // Act
        var result = _validator.Validate(null, ValidOptions());

        // Assert
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_AdminUsernameMissing_FailsNamingThatKey(string? missingValue)
    {
        // Arrange
        var options = ValidOptions();
        options.AdminUsername = missingValue;

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("Wso2:AdminUsername", result.FailureMessage);
        Assert.DoesNotContain("Wso2:AdminPassword", result.FailureMessage);
        Assert.DoesNotContain("Wso2:M2mClientId", result.FailureMessage);
        Assert.DoesNotContain("Wso2:M2mClientSecret", result.FailureMessage);
    }

    [Fact]
    public void Validate_AdminPasswordMissing_FailsNamingThatKey()
    {
        // Arrange
        var options = ValidOptions();
        options.AdminPassword = null;

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("Wso2:AdminPassword", result.FailureMessage);
    }

    [Fact]
    public void Validate_M2mClientIdMissing_FailsNamingThatKey()
    {
        // Arrange
        var options = ValidOptions();
        options.M2mClientId = null;

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("Wso2:M2mClientId", result.FailureMessage);
    }

    [Fact]
    public void Validate_M2mClientSecretMissing_FailsNamingThatKey()
    {
        // Arrange
        var options = ValidOptions();
        options.M2mClientSecret = null;

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("Wso2:M2mClientSecret", result.FailureMessage);
    }

    [Fact]
    public void Validate_AllValuesMissing_FailsNamingEveryKey()
    {
        // Arrange
        var options = new Wso2AdminOptions();

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("Wso2:AdminUsername", result.FailureMessage);
        Assert.Contains("Wso2:AdminPassword", result.FailureMessage);
        Assert.Contains("Wso2:M2mClientId", result.FailureMessage);
        Assert.Contains("Wso2:M2mClientSecret", result.FailureMessage);
    }
}
