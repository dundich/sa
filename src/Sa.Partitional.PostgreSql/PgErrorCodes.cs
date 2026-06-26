using Npgsql;

namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Shared PostgreSQL error code helpers to avoid duplication across services.
/// </summary>
internal static class PgErrorCodes
{
    public static bool IsUndefinedTable(PostgresException ex) =>
        ex.SqlState == PostgresErrorCodes.UndefinedTable
        || ex.SqlState == PostgresErrorCodes.InvalidSchemaName;

    public static bool CanRetryByError(Exception ex)
    {
        if (ex is PostgresException err)
        {
            if (err.IsTransient) return true;

            return err.SqlState switch
            {
                PostgresErrorCodes.ConnectionException
                 or PostgresErrorCodes.ConnectionFailure
                 or PostgresErrorCodes.DeadlockDetected
                 or PostgresErrorCodes.CannotConnectNow
                    => true,
                _ => false,
            };
        }

        // Retry non-Postgres exceptions (e.g. network-level failures)
        return true;
    }
}
