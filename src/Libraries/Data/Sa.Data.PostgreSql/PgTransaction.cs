using Npgsql;

namespace Sa.Data.PostgreSql;

/// <summary>
/// A PostgreSQL transaction opened by <see cref="IPgDataSource.BeginTransaction"/>, together with the
/// connection that owns it. Provides <see cref="CommitAsync(System.Threading.CancellationToken)"/> /
/// <see cref="RollbackAsync(System.Threading.CancellationToken)"/> and disposes the connection it
/// borrowed from the pool.
/// </summary>
public sealed class PgTransaction : IAsyncDisposable
{
    private bool _completed;

    internal PgTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
    }

    /// <summary>An open connection borrowed from the pool.</summary>
    public NpgsqlConnection Connection { get; }

    /// <summary>The active transaction on <see cref="Connection"/>.</summary>
    public NpgsqlTransaction Transaction { get; }

    /// <summary>Commits and releases the transaction. Dispose is then a no-op.</summary>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        await Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _completed = true;
    }

    /// <summary>Rolls back and releases the transaction. Dispose is then a no-op.</summary>
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        await Transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        _completed = true;
    }

    /// <summary>
    /// Rolls back an uncommitted transaction and returns the connection to the pool.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            try
            {
                await Transaction.RollbackAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best effort — the connection is returned to the pool either way.
            }
        }

        await Transaction.DisposeAsync().ConfigureAwait(false);
        await Connection.DisposeAsync().ConfigureAwait(false);
    }
}