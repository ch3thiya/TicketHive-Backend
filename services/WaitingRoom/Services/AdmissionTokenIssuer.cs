namespace WaitingRoom.Service.Services;

public class AdmissionTokenIssuer : IAdmissionTokenIssuer
{
    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid showId, string customerSub, DateTimeOffset admittedAt) =>
        throw new NotImplementedException("Added when admission tokens are implemented.");
}
