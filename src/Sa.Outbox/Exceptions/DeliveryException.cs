namespace Sa.Outbox.Exceptions;

public class DeliveryException(
    string message, 
    Exception? innerException,
    DeliveryStatusCode statusCode, 
    TimeSpan? postponeAt = null): OutboxException(message, innerException)
{
    public DeliveryStatusCode StatusCode => statusCode;
    public TimeSpan? PostponeAt => postponeAt;
}
