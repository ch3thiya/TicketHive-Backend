using Booking.Service.Models;

namespace Booking.Service.Services;

public interface ISalesService
{
    Task<SalesResult> GetShowSalesAsync(Guid showId);
}
