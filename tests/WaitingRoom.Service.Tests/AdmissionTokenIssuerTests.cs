using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using WaitingRoom.Service.Services;
using Xunit;

namespace WaitingRoom.Service.Tests;

public sealed class AdmissionTokenIssuerTests
{
    [Fact]
    public void Issue_AdmittedCustomer_ProducesATokenThatVerifiesAgainstThePublicKey()
    {
        // Arrange
        using var rsa = RSA.Create(2048);
        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        using var issuer = new AdmissionTokenIssuer(Options.Create(new AdmissionTokenOptions
        {
            PrivateKeyPem = privateKeyPem,
            Issuer = "tickethive-waiting-room",
            ExpiryMinutes = 15
        }));

        var showId = Guid.CreateVersion7();
        var customerSub = "customer-abc123";
        var admittedAt = DateTimeOffset.UtcNow;

        // Act
        var (token, expiresAt) = issuer.Issue(showId, customerSub, admittedAt);

        // Assert — the public key alone (no private key) verifies the token
        using var publicKey = RSA.Create();
        publicKey.ImportRSAPublicKey(rsa.ExportRSAPublicKey(), out _);

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "tickethive-waiting-room",
            ValidateAudience = true,
            ValidAudience = showId.ToString(),
            ValidateLifetime = true,
            IssuerSigningKey = new RsaSecurityKey(publicKey)
        }, out var validatedToken);

        Assert.Equal(customerSub, principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value);
        Assert.Equal(expiresAt.UtcDateTime, ((JwtSecurityToken)validatedToken).ValidTo, TimeSpan.FromSeconds(1));
        Assert.Equal(admittedAt.AddMinutes(15), expiresAt);
    }

    [Fact]
    public void Issue_TokenForOneShow_FailsAudienceValidationForAnotherShow()
    {
        // Arrange
        using var rsa = RSA.Create(2048);
        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        using var issuer = new AdmissionTokenIssuer(Options.Create(new AdmissionTokenOptions
        {
            PrivateKeyPem = privateKeyPem,
            Issuer = "tickethive-waiting-room",
            ExpiryMinutes = 15
        }));

        var admittedShowId = Guid.CreateVersion7();
        var otherShowId = Guid.CreateVersion7();
        var (token, _) = issuer.Issue(admittedShowId, "customer-abc123", DateTimeOffset.UtcNow);

        using var publicKey = RSA.Create();
        publicKey.ImportRSAPublicKey(rsa.ExportRSAPublicKey(), out _);

        // Act & Assert — valid for the admitted show only
        Assert.Throws<SecurityTokenInvalidAudienceException>(() =>
            new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "tickethive-waiting-room",
                ValidateAudience = true,
                ValidAudience = otherShowId.ToString(),
                ValidateLifetime = true,
                IssuerSigningKey = new RsaSecurityKey(publicKey)
            }, out _));
    }
}
