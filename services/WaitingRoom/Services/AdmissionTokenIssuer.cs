using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace WaitingRoom.Service.Services;

public class AdmissionTokenIssuer : IAdmissionTokenIssuer, IDisposable
{
    private readonly RSA _privateKey;
    private readonly AdmissionTokenOptions _options;

    public AdmissionTokenIssuer(IOptions<AdmissionTokenOptions> options)
    {
        _options = options.Value;

        if (string.IsNullOrWhiteSpace(_options.PrivateKeyPem))
        {
            throw new InvalidOperationException("Configuration 'AdmissionToken:PrivateKeyPem' is missing.");
        }

        _privateKey = RSA.Create();
        _privateKey.ImportFromPem(_options.PrivateKeyPem);
    }

    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid showId, string customerSub, DateTimeOffset admittedAt)
    {
        var expiresAt = admittedAt.AddMinutes(_options.ExpiryMinutes);
        var credentials = new SigningCredentials(new RsaSecurityKey(_privateKey), SecurityAlgorithms.RsaSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, customerSub)
        };

        // The audience scoped to the show id is what makes the token valid
        // for one show only — Inventory checks ValidAudience against the
        // show it is asked to admit into (verification is the next branch).
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: showId.ToString(),
            claims: claims,
            notBefore: admittedAt.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public void Dispose() => _privateKey.Dispose();
}
