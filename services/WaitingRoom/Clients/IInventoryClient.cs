namespace WaitingRoom.Service.Clients;

public interface IInventoryClient
{
    /// <summary>
    /// True when every ticket category for the show is sold out. Reads
    /// Inventory's public, anonymous availability endpoint — no service
    /// token, never on the join or admission path. Throws
    /// <see cref="InventoryUnavailableException"/> on any failure; the
    /// caller must leave the queue open and try again.
    /// </summary>
    Task<bool> IsSoldOutAsync(Guid showId, CancellationToken cancellationToken = default);
}
