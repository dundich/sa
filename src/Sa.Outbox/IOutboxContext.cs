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
    Guid OutboxId { get; }

    /// <summary>
    /// Gets the unique identifier for the payload.
    /// </summary>
    string PayloadId { get; }

    /// <summary>
    /// Gets information about the part of the Outbox message being processed.
    /// </summary>
    OutboxPartInfo PartInfo { get; }

    /// <summary>
    /// Gets information about the delivery of the Outbox message.
    /// </summary>
    OutboxTaskDeliveryInfo DeliveryInfo { get; }

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
    #region Success Status Operations (2xx)

    /// <summary>
    /// Marks the message processing as successful (200 OK).
    /// </summary>
    /// <param name="message">An optional message providing additional context.</param>
    void Ok(string? message = null);

    /// <summary>
    /// Marks the message processing as successful with resource creation (201 Created).
    /// </summary>
    /// <param name="message">An optional message providing additional context.</param>
    void Created(string? message = null);

    /// <summary>
    /// Marks the message as accepted for asynchronous processing (202 Accepted).
    /// </summary>
    /// <param name="message">An optional message providing additional context.</param>
    void Accepted(string? message = null);

    /// <summary>
    /// Marks the message processing as successful with data transformation (203 Non-Authoritative Information).
    /// </summary>
    /// <param name="message">An optional message providing additional context.</param>
    void Ok203(string? message = null);

    /// <summary>
    /// Marks the message processing as successful with no content to return (204 No Content).
    /// </summary>
    /// <param name="message">An optional message providing additional context.</param>
    void NoContent(string? message = null);

    #endregion

    #region Control Operations (2xx/3xx)

    /// <summary>
    /// Marks the message processing as aborted (299 Aborted).
    /// </summary>
    /// <param name="message">An optional message providing additional context.</param>
    void Aborted(string? message = null);

    /// <summary>
    /// Marks the message as permanently moved to another queue (301 Moved Permanently).
    /// </summary>
    /// <param name="message">An optional message providing additional context.</param>
    void MovedPermanently(string? message = null);

    #endregion

    #region Postponement Operation (1xx)

    /// <summary>
    /// Marks the message processing as postponed (103 Postpone).
    /// </summary>
    /// <param name="postpone">The duration to postpone processing.</param>
    /// <param name="message">An optional message providing additional context.</param>
    void Postpone(TimeSpan postpone, string? message = null);

    #endregion

    #region Client Error Operations (4xx - Recoverable)

    /// <summary>
    /// Marks the message processing with a recoverable warning (400 Warn).
    /// </summary>
    /// <param name="exception">The exception that occurred during processing.</param>
    /// <param name="message">An optional message providing additional context.</param>
    /// <param name="postpone">An optional duration to postpone processing.</param>
    void Warn(Exception exception, string? message = null, TimeSpan? postpone = null);

    #endregion

    #region Server Error Operations (5xx - Permanent)

    /// <summary>
    /// Marks the message processing as a permanent error (500 Internal Server Error).
    /// </summary>
    /// <param name="exception">The exception that occurred during processing.</param>
    /// <param name="message">An optional message providing additional context.</param>
    void Error(Exception exception, string? message = null);

    /// <summary>
    /// Marks the message processing as a 501 Not Implemented error.
    /// </summary>
    void Error501(Exception exception, string? message = null);

    /// <summary>
    /// Marks the message processing as a 502 Bad Gateway error.
    /// </summary>
    void Error502(Exception exception, string? message = null);

    /// <summary>
    /// Marks the message processing as a 503 Service Unavailable error.
    /// </summary>
    void Error503(Exception exception, string? message = null);

    /// <summary>
    /// Marks the message processing as a 504 Gateway Timeout error.
    /// </summary>
    void Error504(Exception exception, string? message = null);

    /// <summary>
    /// Marks the message processing as a 505 HTTP Version Not Supported error.
    /// </summary>
    void Error505(Exception exception, string? message = null);

    /// <summary>
    /// Marks the message processing as a 506 Variant Also Negotiates error.
    /// </summary>
    void Error506(Exception exception, string? message = null);

    /// <summary>
    /// Marks the message processing as a 507 Insufficient Storage error.
    /// </summary>
    void Error507(Exception exception, string? message = null);

    /// <summary>
    /// Marks the message processing as permanently failed due to maximum attempts exceeded (508 Loop Detected).
    /// </summary>
    void ErrorMaxAttempts();

    #endregion

    #region Utility Operations

    /// <summary>
    /// Gets the current UTC date and time.
    /// </summary>
    /// <returns>The current UTC date and time.</returns>
    DateTimeOffset GetUtcNow();

    #endregion
}
