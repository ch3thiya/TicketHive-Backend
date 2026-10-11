namespace Booking.Service.Models;

public class OrderSeat
{
    public Guid OrderId { get; set; }
    public Guid SeatId { get; set; }
    public Guid CategoryId { get; set; }
    public string Section { get; set; } = string.Empty;
    public string Row { get; set; } = string.Empty;
    public int Number { get; set; }
}
