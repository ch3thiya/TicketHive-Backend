using Microsoft.Extensions.Options;

namespace WaitingRoom.Service.Services;

// A service that silently starts without a signing key fails later with a
// confusing crypto error on the first admitted customer's request instead of
// a clear startup message, so the key is required here.
public class AdmissionTokenOptionsValidator : IValidateOptions<AdmissionTokenOptions>
{
    public ValidateOptionsResult Validate(string? name, AdmissionTokenOptions options)
    {
        return string.IsNullOrWhiteSpace(options.PrivateKeyPem)
            ? ValidateOptionsResult.Fail($"Missing required configuration: {AdmissionTokenOptions.SectionName}:{nameof(AdmissionTokenOptions.PrivateKeyPem)}.")
            : ValidateOptionsResult.Success;
    }
}
