using Microsoft.Extensions.Options;

namespace BuildingBlocks;

// A service that silently starts without its M2M credentials fails later
// with a confusing token-request error instead of a clear startup message,
// so every value the token client needs is required here.
public class InternalServiceTokenClientOptionsValidator : IValidateOptions<InternalServiceTokenClientOptions>
{
    public ValidateOptionsResult Validate(string? name, InternalServiceTokenClientOptions options)
    {
        var missingKeys = new List<string>();

        if (string.IsNullOrWhiteSpace(options.TokenEndpoint))
        {
            missingKeys.Add($"{InternalServiceTokenClientOptions.SectionName}:{nameof(InternalServiceTokenClientOptions.TokenEndpoint)}");
        }

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            missingKeys.Add($"{InternalServiceTokenClientOptions.SectionName}:{nameof(InternalServiceTokenClientOptions.ClientId)}");
        }

        if (string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            missingKeys.Add($"{InternalServiceTokenClientOptions.SectionName}:{nameof(InternalServiceTokenClientOptions.ClientSecret)}");
        }

        if (string.IsNullOrWhiteSpace(options.Scope))
        {
            missingKeys.Add($"{InternalServiceTokenClientOptions.SectionName}:{nameof(InternalServiceTokenClientOptions.Scope)}");
        }

        return missingKeys.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Missing required configuration: {string.Join(", ", missingKeys)}.");
    }
}
