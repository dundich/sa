namespace Sa.Outbox.Delivery;

/// <summary>
/// Defines a strategy for calculating retry backoff delays.
/// </summary>
public interface IRetryStrategy
{
    /// <summary>
    /// Calculates the backoff delay for the next retry attempt.
    /// </summary>
    /// <param name="attemptNumber">The current attempt number (1-based). Higher attempts should typically yield longer delays.</param>
    /// <returns>The time span to wait before the next retry.</returns>
    TimeSpan GetBackoff(int attemptNumber);
}
