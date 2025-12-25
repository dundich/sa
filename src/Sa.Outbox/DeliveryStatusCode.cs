namespace Sa.Outbox;


public enum DeliveryStatusCode
{
    /// <summary>
    /// Indicates that the message is pending and has not yet been processed.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Indicates that the message is currently being processed.
    /// </summary>
    Processing = 100,

    /// <summary>
    /// Indicates that the processing of the message has been postponed.
    /// This may occur due to temporary conditions that prevent immediate processing.
    /// </summary>
    Postpone = 103,

    /// <summary>
    /// Indicates that the message has been processed successfully.
    /// </summary>
    Ok = 200,

    /// <summary>
    /// Indicates that the message processing resulted in creation of a new resource.
    /// The new resource is available and its identifier is returned.
    /// Example: Order creation, user registration, document generation.
    /// </summary>
    Created = 201,

    /// <summary>
    /// Indicates that the message has been accepted for processing,
    /// but the processing is not yet complete. The operation will continue
    /// asynchronously or will be completed later.
    /// Example: Long-running report generation, batch processing, async API calls.
    /// </summary>
    Accepted = 202,

    /// <summary>
    /// Indicates that the message has been processed successfully with status 203. 
    /// </summary>
    Ok203 = 203,

    /// <summary>
    /// The operation completed but doesn't require returning data.
    /// Example: Delete operations, cache invalidation, acknowledgment messages.
    /// </summary>
    NoContent = 204,

    /// <summary>
    /// Indicates that the processing of the message has been aborted.
    /// This may happen due to user intervention.
    /// </summary>
    Aborted = 299,

    /// <summary>
    /// Message moved to another queue/handler
    /// HTTP: 301 Moved Permanently
    /// </summary>
    MovedPermanently = 301,

    /// <summary>
    /// Indicates that an error occurred during the processing of the message.
    /// This may include various types of recoverable errors.
    /// </summary>
    Warn = 400,

    /// <summary>
    /// Indicates that a permanent error has occurred, and the message cannot be processed.
    /// </summary>
    Error = 500,

    /// <summary>
    /// Indicates that a permanent error has occurred, and the message cannot be processed.
    /// </summary>
    Error501 = 501,

    /// <summary>
    /// Indicates that a permanent error has occurred, and the message cannot be processed.
    /// </summary>
    Error502 = 502,

    /// <summary>
    /// Indicates that a permanent error has occurred, and the message cannot be processed.
    /// </summary>
    Error503 = 503,

    /// <summary>
    /// Indicates that a permanent error has occurred, and the message cannot be processed.
    /// </summary>
    Error504 = 504,

    /// <summary>
    /// Indicates that a permanent error has occurred, and the message cannot be processed.
    /// </summary>
    Error505 = 505,

    /// <summary>
    /// Indicates that a permanent error has occurred, and the message cannot be processed.
    /// </summary>
    Error506 = 506,

    /// <summary>
    /// Indicates that a permanent error has occurred, and the message cannot be processed.
    /// </summary>
    Error507 = 507,

    /// <summary>
    /// Indicates a permanent error has occurred 
    /// - that the maximum number of processing attempts has been reached,
    /// and the message will not be retried further.
    /// </summary>
    MaximumAttemptsError = 508
}



public static class DeliveryStatusCodeExtensions
{
    public static bool IsPending(this DeliveryStatusCode statusCode) =>
        statusCode == DeliveryStatusCode.Pending;

    public static bool IsProcessing(this DeliveryStatusCode statusCode) =>
        statusCode == DeliveryStatusCode.Processing;

    public static bool IsPostponed(this DeliveryStatusCode statusCode) =>
        statusCode == DeliveryStatusCode.Postpone;

    public static bool IsSuccess(this DeliveryStatusCode statusCode) =>
        statusCode >= DeliveryStatusCode.Ok && statusCode <= DeliveryStatusCode.Aborted;

    public static bool IsAborted(this DeliveryStatusCode statusCode) =>
        statusCode == DeliveryStatusCode.Aborted;

    public static bool IsWarning(this DeliveryStatusCode statusCode) =>
        statusCode >= DeliveryStatusCode.Warn && statusCode < DeliveryStatusCode.Error;

    public static bool IsError(this DeliveryStatusCode statusCode) =>
        statusCode >= DeliveryStatusCode.Error;
}