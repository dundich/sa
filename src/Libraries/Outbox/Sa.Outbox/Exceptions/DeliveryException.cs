using Sa.Outbox.Delivery;

namespace Sa.Outbox.Exceptions;

public class DeliveryException(
    string message,
    Exception? innerException,
    DeliveryStatusCode statusCode,
    TimeSpan? postponeDelay = null) : OutboxException(message, innerException)
{
    public DeliveryStatusCode StatusCode => statusCode;
    public TimeSpan? PostponeDelay => postponeDelay;
}
