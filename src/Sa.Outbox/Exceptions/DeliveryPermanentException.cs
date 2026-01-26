using Sa.Outbox.Delivery;

namespace Sa.Outbox.Exceptions;

public class DeliveryPermanentException(string message, Exception? innerException = null, DeliveryStatusCode statusCode = DeliveryStatusCode.Error)
    : DeliveryException(message, innerException, statusCode)
{
}
