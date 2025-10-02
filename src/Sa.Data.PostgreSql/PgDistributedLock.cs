using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Sa.Data.PostgreSql;

/// <summary>
/// lock by pg_try_advisory_lock
/// </summary>
/// <seealso href="https://www.postgresql.org/docs/9.4/explicit-locking.html#ADVISORY-LOCKS"/>
/// <seealso href="https://ankitvijay.net/2021/02/28/distributed-lock-using-postgresql/"/>
internal sealed partial class PgDistributedLock(PgDataSourceSettings settings, ILogger<PgDistributedLock>? logger = null) : IPgDistributedLock
{
    private readonly ILogger<PgDistributedLock> _logger = logger ?? NullLogger<PgDistributedLock>.Instance;

    private readonly NpgsqlConnectionStringBuilder builder = new(settings.ConnectionString);

    public async Task<bool> TryExecuteInDistributedLock(long lockId, Func<CancellationToken, Task> exclusiveLockTask, CancellationToken cancellationToken)
    {
        LogTryingToAcquireLock(_logger, lockId);

        using var connection = new NpgsqlConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);

        bool hasLockedAcquired = await TryAcquireLockAsync(lockId, connection, cancellationToken);

        if (!hasLockedAcquired)
        {
            LogLockRejected(_logger, lockId);
            return false;
        }

        LogLockAcquired(_logger, lockId);
        try
        {
            if (await TryAcquireLockAsync(lockId, connection, cancellationToken))
            {
                await exclusiveLockTask(cancellationToken);
            }
        }
        finally
        {
            LogReleasingLock(_logger, lockId);
            await ReleaseLock(lockId, connection, cancellationToken);
        }
        return true;
    }

    private static async Task<bool> TryAcquireLockAsync(long lockId, NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        string sessionLockCommand = $"SELECT pg_try_advisory_lock({lockId})";
        using var commandQuery = new NpgsqlCommand(sessionLockCommand, connection);
        object? result = await commandQuery.ExecuteScalarAsync(cancellationToken);
        if (result != null && bool.TryParse(result.ToString(), out var lockAcquired) && lockAcquired)
        {
            return true;
        }
        return false;
    }

    private static async Task ReleaseLock(long lockId, NpgsqlConnection connection, CancellationToken cancellationToke)
    {
        string transactionLockCommand = $"SELECT pg_advisory_unlock({lockId})";
        using var commandQuery = new NpgsqlCommand(transactionLockCommand, connection);
        await commandQuery.ExecuteScalarAsync(cancellationToke);
    }


    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Trace,
        Message = "Trying to acquire session lock for Lock Id {LockId}")]
    static partial void LogTryingToAcquireLock(ILogger logger, long lockId);
    
    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Lock {LockId} rejected")]
    static partial void LogLockRejected(ILogger logger, long lockId);
    
    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Information,
        Message = "Lock {LockId} acquired")]
    static partial void LogLockAcquired(ILogger logger, long lockId);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Information,
        Message = "Releasing session lock for {LockId}")]
    static partial void LogReleasingLock(ILogger logger, long lockId);
}
