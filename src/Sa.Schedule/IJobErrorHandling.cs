namespace Sa.Schedule;

/// <summary>
/// Defines the error handling behavior for a job.
/// </summary>
public interface IJobErrorHandling
{
    /// <summary>
    /// Gets the action to take after an error occurs.
    /// </summary>
    ErrorHandlingAction ThenAction { get; }

    /// <summary>
    /// Gets the number of retry attempts.
    /// </summary>
    int RetryCount { get; }

    /// <summary>
    /// Gets a function that determines whether to suppress an error.
    /// </summary>
    Func<Exception, bool>? SuppressError { get; }

    /// <summary>
    /// Gets a value indicating whether an error suppression function is defined.
    /// </summary>
    bool HasSuppressError => SuppressError != null;

    /// <summary>
    /// Gets a value indicating whether retry attempts are enabled.
    /// </summary>
    bool HasRetryAttempts => RetryCount > 0;
}
