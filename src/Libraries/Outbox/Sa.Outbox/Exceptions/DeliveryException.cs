using Sa.Outbox.Delivery;

namespace Sa.Outbox.Exceptions;

/// <summary>
/// A message delivery failed with an explicit delivery status code.
/// </summary>
/// <param name="message">The error message.</param>
/// <param name="innerException">The underlying failure, if any.</param>
/// <param name="statusCode">The status code that describes the failure.</param>
/// <param name="postponeDelay">When set, the message becomes available again after this delay.</param>
public class DeliveryException(
    string message,
    Exception? innerException,
    DeliveryStatusCode statusCode,
    TimeSpan? postponeDelay = null) : OutboxException(message, innerException)
{
    /// <summary>The status code that describes the failure.</summary>
    public DeliveryStatusCode StatusCode => statusCode;

    /// <summary>When set, the message becomes available again after this delay.</summary>
    public TimeSpan? PostponeDelay => postponeDelay;
}
