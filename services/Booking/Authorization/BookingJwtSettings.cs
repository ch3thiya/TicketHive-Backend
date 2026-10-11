using Microsoft.Extensions.Configuration;

namespace Booking.Service.Authorization;

/// <summary>
/// Token-validation settings for Booking's two JWT schemes. User tokens (customers,
/// organizers, admins) and internal machine-to-machine tokens are accepted only for
/// explicitly configured audiences; nothing is ever accepted by default.
/// </summary>
public sealed record BookingJwtSettings(
    string Authority,
    IReadOnlyList<string> UserAudiences,
    IReadOnlyList<string> InternalAudiences)
{
    public const string UserScheme = "Bearer";
    public const string InternalScheme = "Internal";

    /// <summary>RFC 9068 access-token type. Asgardeo ID tokens use "JWT" and are rejected.</summary>
    public const string AccessTokenType = "at+jwt";

    public static BookingJwtSettings Load(IConfiguration configuration)
    {
        var authority = configuration["Jwt:Authority"]?.Trim();
        // Jwt:Audience is the original single-value key; it still feeds the user allowlist.
        var user = ReadList(configuration, "Jwt:UserAudiences").Concat(ReadList(configuration, "Jwt:Audience"));
        var settings = new BookingJwtSettings(
            authority ?? string.Empty,
            user.Distinct(StringComparer.Ordinal).ToArray(),
            ReadList(configuration, "Jwt:InternalAudiences").Distinct(StringComparer.Ordinal).ToArray());
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (!Uri.TryCreate(Authority, UriKind.Absolute, out _))
            throw new InvalidOperationException("Configuration 'Jwt:Authority' must be an absolute URL.");
        RequireAudiences(UserAudiences, "Jwt:UserAudiences");
        RequireAudiences(InternalAudiences, "Jwt:InternalAudiences");

        var shared = UserAudiences.Intersect(InternalAudiences, StringComparer.Ordinal).FirstOrDefault();
        if (shared is not null)
            throw new InvalidOperationException(
                "Jwt:UserAudiences and Jwt:InternalAudiences must not share an audience; a shared value would let one token type pass both schemes.");
    }

    private static void RequireAudiences(IReadOnlyList<string> audiences, string key)
    {
        if (audiences.Count == 0)
            throw new InvalidOperationException($"Configuration '{key}' must list at least one audience.");
        foreach (var audience in audiences)
        {
            if (audience.Contains('*') || audience.Contains('?') || audience.Any(char.IsWhiteSpace))
                throw new InvalidOperationException($"Configuration '{key}' contains an invalid audience; wildcards and whitespace are not allowed.");
        }
    }

    // Accepts an array section (Key:0, Key:1) or a comma-separated string.
    private static IEnumerable<string> ReadList(IConfiguration configuration, string key)
    {
        var section = configuration.GetSection(key);
        var values = section.GetChildren().Select(c => c.Value).Append(section.Value);
        return values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .SelectMany(v => v!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
