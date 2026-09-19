using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Inventory.Service.Tests.Api;

/// <summary>
/// One RSA key pair, generated once per test run, standing in for the
/// admission-token signing key the real WaitingRoom service holds in
/// production. Its public half is what the Inventory test hosts configure
/// as AdmissionToken:PublicKeyPem (see HoldsApiFactory, InventoryApiFactory);
/// <see cref="MintToken"/> signs with the private half the same way
/// WaitingRoom.Service.Services.AdmissionTokenIssuer does — RS256, a "sub"
/// claim for the customer, the show id as the audience.
/// </summary>
public static class AdmissionTokenTestKeys
{
    public const string Issuer = "tickethive-waiting-room";

    private static readonly RSA SigningKey = RSA.Create(2048);

    public static string PublicKeyPem => SigningKey.ExportSubjectPublicKeyInfoPem();

    public static string MintToken(
        Guid showId,
        string customerSub,
        DateTimeOffset notBefore,
        TimeSpan? lifetime = null,
        RSA? signAs = null,
        string issuer = Issuer)
    {
        var key = signAs ?? SigningKey;
        var expires = notBefore.Add(lifetime ?? TimeSpan.FromMinutes(15));
        var credentials = new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: showId.ToString(),
            claims: new[] { new Claim(JwtRegisteredClaimNames.Sub, customerSub) },
            notBefore: notBefore.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
