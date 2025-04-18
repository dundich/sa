namespace Sa.Outbox.Exceptions;

public class DeliveryPermanentException(string message, Exception? innerException = null, int statusCode = 500)
    : DeliveryException(message, innerException, statusCode)
{
}
