using Sa.Outbox.Delivery;

namespace Sa.Outbox.Exceptions;

/// <summary>
/// A delivery failure that must not be retried: the message is parked with a terminal status.
/// </summary>
/// <param name="message">The error message.</param>
/// <param name="innerException">The underlying failure, if any.</param>
/// <param name="statusCode">The terminal status code to persist for the message.</param>
public class DeliveryPermanentException(
    string message,
    Exception? innerException = null,
    DeliveryStatusCode statusCode = DeliveryStatusCode.Error) : DeliveryException(message, innerException, statusCode)
{
}
