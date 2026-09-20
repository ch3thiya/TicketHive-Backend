using System;
using System.Threading.Tasks;

namespace Booking.Service.Clients;

public interface IInventoryClient
{
    Task<InventoryHoldResponse?> GetHoldAsync(Guid holdId);
    Task<bool> FreezeHoldAsync(Guid holdId);
    Task<bool> ConvertHoldAsync(Guid holdId);
    Task<bool> ReleaseHoldAsync(Guid holdId);
}
