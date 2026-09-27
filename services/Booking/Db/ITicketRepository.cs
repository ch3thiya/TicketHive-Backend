using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Booking.Service.Models;

namespace Booking.Service.Db;

public interface ITicketRepository
{
    Task CreateTicketsAsync(IEnumerable<Ticket> tickets);
    Task<IReadOnlyList<Ticket>> GetByOrderIdAsync(Guid orderId);
    Task<IReadOnlyList<Ticket>> GetByCustomerSubAsync(string customerSub);
    Task<Ticket?> GetByIdAsync(Guid ticketId, string customerSub);
    Task<Ticket?> GetByCodeAsync(string uniqueCode);
    Task<bool> ValidateAndUseTicketAsync(string uniqueCode, string organizerSub, DateTimeOffset usedAt);
}
