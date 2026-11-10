namespace Sa.Outbox.Exceptions;

/// <summary>
/// Base type for every error raised by the outbox.
/// </summary>
public class OutboxException : Exception
{
    /// <summary>Initializes a new instance with no message.</summary>
    public OutboxException()
    {
    }

    /// <summary>Initializes a new instance with the given message.</summary>
    /// <param name="message">The error message.</param>
    public OutboxException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance with the given message and underlying failure.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public OutboxException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
