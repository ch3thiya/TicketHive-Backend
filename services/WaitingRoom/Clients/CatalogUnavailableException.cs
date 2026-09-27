namespace WaitingRoom.Service.Clients;

// A distinct type so the join flow can catch exactly this failure — timeout,
// non-success status or token acquisition failure — and map it to 503,
// never a generic HttpRequestException (mirrors Catalog's own InventoryUnavailableException).
public class CatalogUnavailableException : Exception
{
    public CatalogUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
