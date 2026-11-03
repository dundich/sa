using Npgsql;
using Sa.Data.PostgreSql;
using System.Collections.Concurrent;

namespace Sa.Outbox.PostgreSql.Services;

/// <summary>
/// Default <see cref="IPgOutboxDatabaseGuard"/>: compares the (host, port, database) identity of the
/// transaction's connection with the identity of the Outbox data source.
/// </summary>
internal sealed class PgOutboxDatabaseGuard : IPgOutboxDatabaseGuard
{
    private readonly string _outboxIdentity;

    /// <summary>
    /// Parsed identities keyed by the connection string they came from. The guard is a singleton
    /// and the transaction side is checked on every transactional publish, so rebuilding the
    /// identity meant parsing the connection string and interpolating it on each of those calls.
    /// A process has one pool — a handful of distinct strings at most — so this stays bounded.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _identities = new(StringComparer.Ordinal);

    public PgOutboxDatabaseGuard(IPgDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _outboxIdentity = BuildIdentity(dataSource.GetConnectionString());
    }

    public void EnsureSameDatabase(NpgsqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        NpgsqlConnection connection = transaction.Connection
            ?? throw new InvalidOperationException(
                "The supplied transaction is not associated with a connection.");

        string targetIdentity = _identities.GetOrAdd(connection.ConnectionString, BuildIdentity);

        if (!string.Equals(_outboxIdentity, targetIdentity, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The supplied transaction belongs to a different database than the Outbox. " +
                $"Outbox: {_outboxIdentity}. Transaction: {targetIdentity}.");
        }
    }

    private static string BuildIdentity(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return $"Host={builder.Host};Port={builder.Port};Database={builder.Database}";
    }
}
