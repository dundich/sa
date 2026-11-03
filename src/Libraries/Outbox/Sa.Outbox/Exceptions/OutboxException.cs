namespace Sa.Outbox.Exceptions;

public class OutboxException : Exception
{
    public OutboxException()
    {
    }

    public OutboxException(string message) : base(message)
    {
    }

    public OutboxException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
