using Npgsql;

namespace Sa.Data.PostgreSql;

public static class PgRetryStrategy
{
    public static ValueTask<T> ExecuteWithRetry<T>(
        Func<CancellationToken, ValueTask<T>> fun,
        int retryCount = 3,
        int initialDelay = 530,
        Func<Exception, int, bool>? next = null,
        CancellationToken cancellationToken = default)
    {
        return Classes.Retry.Jitter(
            fun: fun,
            retryCount: retryCount,
            initialDelay: initialDelay
            , next: (ex, i) => next != null ? next(ex, i) : (ex is NpgsqlException exception) && exception.IsTransient
            , cancellationToken: cancellationToken);
    }
}
