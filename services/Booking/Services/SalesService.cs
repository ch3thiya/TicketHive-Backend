using Booking.Service.Db;
using Booking.Service.Models;

namespace Booking.Service.Services;

public class SalesService(ISalesRepository repository, TimeProvider timeProvider) : ISalesService
{
    public async Task<SalesResult> GetShowSalesAsync(Guid showId)
    {
        var (sold, used) = await repository.GetAsync(showId);
        return ShowSalesCalculator.Compose(showId, sold, used, timeProvider.GetUtcNow());
    }
}
