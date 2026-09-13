using Microsoft.Extensions.Options;

namespace Identity.Service.Clients;

// A service that silently starts with missing WSO2 credentials fails later
// with a confusing SCIM auth error instead of a clear startup message, so
// every credential this service needs to talk to Asgardeo is required here.
public class Wso2AdminOptionsValidator : IValidateOptions<Wso2AdminOptions>
{
    public ValidateOptionsResult Validate(string? name, Wso2AdminOptions options)
    {
        var missingKeys = new List<string>();

        if (string.IsNullOrWhiteSpace(options.AdminUsername))
        {
            missingKeys.Add($"{Wso2AdminOptions.SectionName}:{nameof(Wso2AdminOptions.AdminUsername)}");
        }

        if (string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            missingKeys.Add($"{Wso2AdminOptions.SectionName}:{nameof(Wso2AdminOptions.AdminPassword)}");
        }

        if (string.IsNullOrWhiteSpace(options.M2mClientId))
        {
            missingKeys.Add($"{Wso2AdminOptions.SectionName}:{nameof(Wso2AdminOptions.M2mClientId)}");
        }

        if (string.IsNullOrWhiteSpace(options.M2mClientSecret))
        {
            missingKeys.Add($"{Wso2AdminOptions.SectionName}:{nameof(Wso2AdminOptions.M2mClientSecret)}");
        }

        return missingKeys.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Missing required configuration: {string.Join(", ", missingKeys)}.");
    }
}
