namespace Booking.Service.Clients;

public enum EntryAccessDecision
{
    Allowed,
    Denied,
    Unavailable
}

public interface IEntryAccessClient
{
    /// <summary>
    /// Asks Catalog whether the subject owns the show and so may validate its tickets. Never
    /// cached, so ownership is checked on every validation. Anything other than a clear "allowed"
    /// or "denied" answer, including timeouts and an open circuit, is
    /// <see cref="EntryAccessDecision.Unavailable"/>; callers must treat it as a refusal.
    /// </summary>
    Task<EntryAccessDecision> CheckAsync(Guid showId, string sub, CancellationToken cancellationToken = default);
}