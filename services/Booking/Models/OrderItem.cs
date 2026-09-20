using System;

namespace Booking.Service.Models;

public class OrderItem
{
    public Guid OrderId { get; set; }
    public Guid CategoryId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
