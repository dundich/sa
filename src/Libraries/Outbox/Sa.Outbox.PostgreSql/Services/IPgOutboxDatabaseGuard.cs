using Npgsql;

namespace Sa.Outbox.PostgreSql.Services;

/// <summary>
/// Verifies that a caller-supplied transaction targets the same PostgreSQL database as the Outbox.
/// </summary>
internal interface IPgOutboxDatabaseGuard
{
    /// <summary>
    /// Throws when <paramref name="transaction"/> belongs to a different database (host/port/database)
    /// than the Outbox data source, so messages are never written to a foreign connection.
    /// </summary>
    void EnsureSameDatabase(NpgsqlTransaction transaction);
}
