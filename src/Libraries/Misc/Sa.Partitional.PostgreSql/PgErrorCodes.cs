using Npgsql;
using System.Net.Sockets;

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

        // Only transport-level failures are worth retrying. Everything else (a name over the
        // 63-byte identifier limit, a missing table in the configuration, an argument error) is a
        // programming or configuration problem: retrying it three times just delays the report.
        return ex is NpgsqlException
            or SocketException
            or IOException
            or TimeoutException;
    }
}
