namespace WaitingRoom.Service.Clients;

// A distinct type so the sell-out checker can catch exactly this failure and
// leave the queue open, never closing it because a health check failed.
public class InventoryUnavailableException : Exception
{
    public InventoryUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
