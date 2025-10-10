namespace Sa.Outbox;


public static class DeliveryStatusCode
{
    /// <summary>
    /// Indicates that the message is pending and has not yet been processed.
    /// </summary>
    public const int Pending = 0;

    /// <summary>
    /// Indicates that the message is currently being processed.
    /// </summary>
    public const int Processing = 100;

    /// <summary>
    /// Indicates that the processing of the message has been postponed.
    /// This may occur due to temporary conditions that prevent immediate processing.
    /// </summary>
    public const int Postpone = 103;

    /// <summary>
    /// Indicates that the message has been processed successfully.
    /// </summary>
    public const int Ok = 200;

    /// <summary>
    /// Indicates that the processing of the message has been aborted.
    /// This may happen due to user intervention.
    /// </summary>
    public const int Aborted = 299;

    /// <summary>
    /// Reserved for future use or specific status codes that may be defined later.
    /// </summary>
    public const int Status300 = 300;

    /// <summary>
    /// Indicates that an error occurred during the processing of the message.
    /// This may include various types of recoverable errors.
    /// </summary>
    public const int Error = 400;

    /// <summary>
    /// Reserved for client-side errors that do not fall into other categories.
    /// </summary>
    public const int Status499 = 499;

    /// <summary>
    /// Indicates that a permanent error has occurred, and the message cannot be processed.
    /// </summary>
    public const int PermanentError = 500;

    /// <summary>
    /// Indicates a permanent error has occurred 
    /// - that the maximum number of processing attempts has been reached,
    /// and the message will not be retried further.
    /// </summary>
    public const int MaximumAttemptsError = 501;
}
