using Microsoft.Extensions.Logging;
using Npgsql;

namespace Sa.Data.PostgreSql;

/// <summary>
/// lock by pg_try_advisory_lock
/// </summary>
/// <seealso href="https://www.postgresql.org/docs/9.4/explicit-locking.html#ADVISORY-LOCKS"/>
/// <seealso href="https://ankitvijay.net/2021/02/28/distributed-lock-using-postgresql/"/>
internal sealed class PgDistributedLock(PgDataSourceSettings settings, ILogger<PgDistributedLock>? logger = null) : IPgDistributedLock
{
    private readonly NpgsqlConnectionStringBuilder builder = new(settings.ConnectionString);

    public async Task<bool> TryExecuteInDistributedLock(long lockId, Func<CancellationToken, Task> exclusiveLockTask, CancellationToken cancellationToken)
    {
        logger?.LogInformation("Trying to acquire session lock for Lock Id {@LockId}", lockId);

        using var connection = new NpgsqlConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);

        bool hasLockedAcquired = await TryAcquireLockAsync(lockId, connection, cancellationToken);

        if (!hasLockedAcquired)
        {
            logger?.LogInformation("Lock {@LockId} rejected", lockId);
            return false;
        }

        logger?.LogInformation("Lock {@LockId} acquired", lockId);
        try
        {
            if (await TryAcquireLockAsync(lockId, connection, cancellationToken))
            {
                await exclusiveLockTask(cancellationToken);
            }
        }
        finally
        {
            logger?.LogInformation("Releasing session lock for {@LockId}", lockId);
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
}