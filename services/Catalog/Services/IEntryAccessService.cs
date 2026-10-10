using Catalog.Service.Models;

namespace Catalog.Service.Services;

public interface IEntryAccessService
{
    /// <summary>
    /// Decides whether the signed-in user with this subject may validate tickets at the show:
    /// they must be the organizer that owns it. A suspended organizer keeps this right, because
    /// suspension never voids tickets or stops entry. The organizer is always looked up live in
    /// Identity, never cached, and an unverifiable answer is reported as
    /// <see cref="EntryAccessOutcome.OrganizerStatusUnavailable"/> (never as allowed).
    /// Door staff accounts are not modelled yet; this is the single place to add them.
    /// </summary>
    Task<EntryAccessResult> CheckAsync(Guid showId, string sub, CancellationToken cancellationToken = default);
}