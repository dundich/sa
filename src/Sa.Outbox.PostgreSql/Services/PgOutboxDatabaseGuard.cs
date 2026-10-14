using Npgsql;
using Sa.Data.PostgreSql;

namespace Sa.Outbox.PostgreSql.Services;

/// <summary>
/// Default <see cref="IPgOutboxDatabaseGuard"/>: compares the (host, port, database) identity of the
/// transaction's connection with the identity of the Outbox data source.
/// </summary>
internal sealed class PgOutboxDatabaseGuard : IPgOutboxDatabaseGuard
{
    private readonly string _outboxIdentity;

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

        string targetIdentity = BuildIdentity(connection.ConnectionString);

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
