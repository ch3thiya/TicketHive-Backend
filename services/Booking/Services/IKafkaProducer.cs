using System.Threading.Tasks;

namespace Booking.Service.Services;

public interface IKafkaProducer
{
    Task PublishOrderConfirmedAsync(object orderConfirmedEvent);
}
