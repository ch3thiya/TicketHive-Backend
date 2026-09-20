namespace WaitingRoom.Service.Services;

public interface IAdmissionTokenIssuer
{
    /// <summary>
    /// Mints a signed token for a customer already admitted to a show, valid
    /// for one show only and expiring about 15 minutes after admittedAt.
    /// </summary>
    (string Token, DateTimeOffset ExpiresAt) Issue(Guid showId, string customerSub, DateTimeOffset admittedAt);
}
