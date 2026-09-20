using Microsoft.Extensions.Options;

namespace Inventory.Service.Services;

// A service that silently starts without the admission-token public key
// fails later with a confusing crypto error on the first high-demand hold
// instead of a clear startup message, so the key is required here — and a
// missing key must never fall back to skipping verification (that fallback
// is exactly the bug this replaces; see HoldService).
public class AdmissionTokenOptionsValidator : IValidateOptions<AdmissionTokenOptions>
{
    public ValidateOptionsResult Validate(string? name, AdmissionTokenOptions options)
    {
        return string.IsNullOrWhiteSpace(options.PublicKeyPem)
            ? ValidateOptionsResult.Fail($"Missing required configuration: {AdmissionTokenOptions.SectionName}:{nameof(AdmissionTokenOptions.PublicKeyPem)}.")
            : ValidateOptionsResult.Success;
    }
}
