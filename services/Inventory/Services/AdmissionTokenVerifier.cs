using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Inventory.Service.Services;

public class AdmissionTokenVerifier : IAdmissionTokenVerifier
{
    // Pinned explicitly so a token can't switch to "none" or an HMAC
    // algorithm keyed with the public key bytes (the classic RS256 -> HS256
    // confusion attack) and still validate against this key.
    private static readonly string[] AllowedAlgorithms = { SecurityAlgorithms.RsaSha256 };

    private readonly JwtSecurityTokenHandler _handler = new();
    private readonly TokenValidationParameters _validationParameters;

    public AdmissionTokenVerifier(string publicKeyPem, string issuer, TimeProvider timeProvider)
    {
        var publicKey = RSA.Create();
        publicKey.ImportFromPem(publicKeyPem);

        _validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new RsaSecurityKey(publicKey),
            ValidAlgorithms = AllowedAlgorithms,
            ValidateIssuer = true,
            ValidIssuer = issuer,
            // The audience is the show id, which varies per call, so it is
            // checked manually below alongside the subject rather than
            // through a single fixed ValidAudience.
            ValidateAudience = false,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.Zero,
            // TokenValidationParameters has no TimeProvider hook, so "now"
            // for expiry is supplied explicitly here instead of letting the
            // library fall back to the system clock on its own.
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                if (expires is null)
                {
                    return false;
                }

                var now = timeProvider.GetUtcNow().UtcDateTime;
                return (notBefore is null || notBefore <= now) && now <= expires;
            }
        };
    }

    public bool Verify(Guid showId, string customerSub, string? admissionToken)
    {
        if (string.IsNullOrWhiteSpace(admissionToken))
        {
            return false;
        }

        SecurityToken validatedToken;
        try
        {
            // The ClaimsPrincipal this returns is discarded: JwtSecurityTokenHandler
            // remaps short claim names like "sub" onto long ClaimTypes URIs by
            // default, so the subject is read from the token's own payload
            // below instead of the (re-mapped) principal.
            _handler.ValidateToken(admissionToken, _validationParameters, out validatedToken);
        }
        catch (SecurityTokenException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // Malformed input (not a well-formed compact JWT) throws
            // ArgumentException rather than a SecurityTokenException.
            return false;
        }

        var jwt = (JwtSecurityToken)validatedToken;
        var audience = jwt.Audiences.FirstOrDefault();

        return audience == showId.ToString() && jwt.Subject == customerSub;
    }
}
