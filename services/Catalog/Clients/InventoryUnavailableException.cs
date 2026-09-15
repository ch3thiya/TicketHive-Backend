using System;

namespace Catalog.Service.Clients;

// A distinct type so the publish flow can catch exactly this failure —
// timeout, non-success status or token acquisition failure — and map it to
// 503, separately from the organizer's own validation errors (SCRUM-8).
public class InventoryUnavailableException : Exception
{
    public InventoryUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
