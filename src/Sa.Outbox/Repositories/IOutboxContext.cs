namespace Sa.Outbox;


/// <summary>
/// Represents the context for an Outbox message processing operation.
/// This interface provides information about the message, its delivery status, and methods to update the status.
/// </summary>
public interface IOutboxContext
{
    /// <summary>
    /// Gets the unique identifier for the Outbox message.
    /// </summary>
    string OutboxId { get; }

    /// <summary>
    /// Gets information about the part of the Outbox message being processed.
    /// </summary>
    OutboxPartInfo PartInfo { get; }

    /// <summary>
    /// Gets information about the delivery of the Outbox message.
    /// </summary>
    OutboxDeliveryInfo DeliveryInfo { get; }

    /// <summary>
    /// Gets the result of the delivery attempt.
    /// </summary>
    DeliveryStatus DeliveryResult { get; }

    /// <summary>
    /// Gets any exception that occurred during the processing of the message.
    /// </summary>
    Exception? Exception { get; }

    /// <summary>
    /// Gets the duration for which the message processing is postponed.
    /// </summary>
    TimeSpan PostponeAt { get; }
}


/// <summary>
/// Represents the context for an Outbox message processing operation with a specific message type.
/// This interface extends the <see cref="IOutboxContext"/> to include the message payload.
/// </summary>
/// <typeparam name="TMessage">The type of the message being processed.</typeparam>
public interface IOutboxContext<out TMessage> : IOutboxContext
{
    /// <summary>
    /// Gets the payload of the Outbox message being processed.
    /// </summary>
    TMessage Payload { get; }
}


/// <summary>
/// Provides operations to update the status of an Outbox message processing.
/// </summary>
public interface IOutboxContextOperations<TMessage> : IOutboxContext<TMessage>
{
    /// <summary>
    /// Marks the message processing as successful.
    /// </summary>
    /// <param name="message">An optional message providing additional context.</param>
    /// <returns>The current Outbox context.</returns>
    void Ok(string? message = null);

    /// <summary>
    /// Marks the message processing as aborted.
    /// </summary>
    /// <param name="message">An optional message providing additional context.</param>
    /// <returns>The current Outbox context.</returns>
    void Aborted(string? message = null);

    /// <summary>
    /// Marks the message processing as an error.
    /// </summary>
    /// <param name="exception">The exception that occurred during processing.</param>
    /// <param name="message">An optional message providing additional context.</param>
    /// <param name="statusCode">The status code associated with the error.</param>
    /// <param name="postpone">An optional duration to postpone processing.</param>
    /// <returns>The current Outbox context.</returns>
    void Error(Exception exception, string? message = null, int statusCode = DeliveryStatusCode.Error, TimeSpan? postpone = null);

    /// <summary>
    /// Marks the message processing as a permanent error.
    /// </summary>
    /// <param name="exception">The exception that occurred during processing.</param>
    /// <param name="message">An optional message providing additional context.</param>
    /// <param name="statusCode">The status code associated with the permanent error.</param>
    /// <returns>The current Outbox context.</returns>
    void PermanentError(Exception exception, string? message = null, int statusCode = DeliveryStatusCode.PermanentError);

    /// <summary>
    /// Marks the message processing as postponed.
    /// </summary>
    /// <param name="postpone">The duration to postpone processing.</param>
    /// <param name="message">An optional message providing additional context.</param>
    /// <returns>The current Outbox context.</returns>
    void Postpone(TimeSpan postpone, string? message = null);
}
