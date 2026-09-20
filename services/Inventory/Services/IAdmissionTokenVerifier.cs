using System;

namespace Inventory.Service.Services;

public interface IAdmissionTokenVerifier
{
    /// <summary>
    /// Verifies an admission token locally against the configured public
    /// key: a valid RS256 signature, the expected issuer, an unexpired
    /// lifetime, the show id as the audience and the caller as the subject.
    /// Never calls the waiting room — that is the entire point of an
    /// asymmetrically signed token (ADR-002).
    /// </summary>
    bool Verify(Guid showId, string customerSub, string? admissionToken);
}
