using Xunit;
using WaitingRoom.Service.Services;

namespace WaitingRoom.Service.Tests;

public class AdmissionTokenOptionsValidatorTests
{
    private readonly AdmissionTokenOptionsValidator _validator = new();

    [Fact]
    public void Validate_PrivateKeyPemPresent_ReturnsSuccess()
    {
        // Arrange
        var options = new AdmissionTokenOptions { PrivateKeyPem = "-----BEGIN PRIVATE KEY-----\n...\n-----END PRIVATE KEY-----\n" };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_PrivateKeyPemMissing_FailsNamingTheKey(string? missingValue)
    {
        // Arrange
        var options = new AdmissionTokenOptions { PrivateKeyPem = missingValue! };

        // Act
        var result = _validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("AdmissionToken:PrivateKeyPem", result.FailureMessage);
    }
}
