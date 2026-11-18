using Npgsql;

namespace Sa.Data.PostgreSql;

/// <summary>
/// Retry helper for transient PostgreSQL failures.
/// </summary>
public static class PgRetryStrategy
{
    /// <summary>
    /// Executes the given operation with jittered retries for transient Npgsql errors.
    /// </summary>
    /// <typeparam name="T">The result type of the operation.</typeparam>
    /// <param name="fun">The operation to execute.</param>
    /// <param name="retryCount">The maximum number of retries.</param>
    /// <param name="medianFirstRetryDelay">The median delay, in milliseconds, before the first retry.</param>
    /// <param name="next">An optional predicate deciding whether an exception should be retried.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The result of the operation.</returns>
    public static ValueTask<T> ExecuteWithRetry<T>(
        Func<CancellationToken, ValueTask<T>> fun,
        int retryCount = 3,
        int medianFirstRetryDelay = 530,
        Func<Exception, int, bool>? next = null,
        CancellationToken cancellationToken = default)
    {
        return Retry.Jitter(
            fun: fun,
            retryCount: retryCount,
            medianFirstRetryDelay: medianFirstRetryDelay,
            shouldRetry: (ex, i) => next != null
                ? next(ex, i)
                : (ex is NpgsqlException exception) && exception.IsTransient,
            cancellationToken: cancellationToken);
    }
}
