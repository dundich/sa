namespace Sa.Outbox.Exceptions;

public class DeliveryException(
    string message, 
    Exception? innerException = null, 
    int statusCode = 400, 
    TimeSpan? postponeAt = null): OutboxException(message, innerException)
{
    public int StatusCode => statusCode;
    public TimeSpan? PostponeAt => postponeAt;
}
