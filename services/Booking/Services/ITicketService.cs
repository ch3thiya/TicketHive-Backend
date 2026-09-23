using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Booking.Service.Models;

namespace Booking.Service.Services;

public interface ITicketService
{
    Task<IReadOnlyList<TicketResponse>> IssueTicketsForOrderAsync(Order order);
    Task<IReadOnlyList<TicketResponse>> GetCustomerTicketsAsync(string customerSub);
    Task<TicketResponse?> GetTicketByIdAsync(Guid ticketId, string customerSub);
    Task<IReadOnlyList<TicketResponse>> GetOrderTicketsAsync(Guid orderId, string customerSub);
    TicketResponse ToResponse(Ticket ticket);
}
