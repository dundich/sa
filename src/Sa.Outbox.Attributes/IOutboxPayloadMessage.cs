namespace Sa.Outbox.Support;

/// <summary>
/// Represents a message payload in the Outbox system.
/// This interface defines the properties that any Outbox payload message must implement.
/// </summary>
public interface IOutboxPayloadMessage
{
    /// <summary>
    /// Gets the unique identifier for the payload.
    /// </summary>
    string Message { get; }

    /// <summary>
    /// Gets the identifier for the tenant associated with the payload.
    /// </summary>
    public int TenantId { get; }
}
