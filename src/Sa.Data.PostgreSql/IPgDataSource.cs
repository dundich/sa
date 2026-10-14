using Npgsql;

namespace Sa.Data.PostgreSql;

public interface IPgDataSource : IDisposable, IAsyncDisposable
{
    public static IPgDataSource Create(string connectionString)
        => new PgDataSource(connectionString);

    public static IPgDataSource Create(NpgsqlDataSource dataSource)
        => new PgDataSource(dataSource);

    string GetSearchPath();

    /// <summary>
    /// Returns the connection string of the underlying data source.
    /// </summary>
    string GetConnectionString();

    ValueTask<NpgsqlConnection> OpenDbConnection(CancellationToken cancellationToken);

    /// <summary>
    /// Opens a new connection and starts a transaction on it, returning a <see cref="PgTransaction"/>
    /// that owns both and returns the connection to the pool on dispose. Useful when a unit of work
    /// must span several components sharing the same database (for example, an Outbox write
    /// committed atomically with the caller's own changes).
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation
    /// requests.</param>
    ValueTask<PgTransaction> BeginTransaction(CancellationToken cancellationToken = default);

    Task<int> ExecuteNonQuery(
        string sql,
        Action<NpgsqlCommand>? initCommand,
        CancellationToken cancellationToken = default);

    Task<int> ExecuteNonQuery(
        string sql,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default);

    Task<int> ExecuteNonQuery(string sql, CancellationToken cancellationToken = default);

    Task<object?> ExecuteScalar(
        string sql, Action<NpgsqlCommand>? initCommand, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a scalar query with no parameters.
    /// </summary>
    Task<object?> ExecuteScalar(string sql, CancellationToken cancellationToken = default);

    Task<T> ExecuteScalar<T>(
        string sql, Action<NpgsqlCommand>? initCommand, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a typed scalar query.
    /// </summary>
    Task<T> ExecuteScalarTyped<T>(
        string sql, Action<NpgsqlCommand>? initCommand = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a typed scalar query with parameters.
    /// </summary>
    Task<T> ExecuteScalarTyped<T>(
        string sql,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default);

    // ExecuteReader
    Task<int> ExecuteReader(
        string sql,
        Action<NpgsqlDataReader, int> read,
        Action<NpgsqlCommand>? initCommand,
        CancellationToken cancellationToken = default);

    Task<int> ExecuteReader(
        string sql,
        Action<NpgsqlDataReader, int> read,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default);

    Task<int> ExecuteReader(
        string sql, Action<NpgsqlDataReader, int> read, CancellationToken cancellationToken = default);

    // ExecuteReaderList
    Task<List<T>> ExecuteReaderList<T>(
        string sql, Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken = default);

    Task<List<T>> ExecuteReaderList<T>(
        string sql,
        Func<NpgsqlDataReader, T> read,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default);

    // ExecuteReaderFirst

    /// <summary>
    /// Reads the value of the first column of the <b>first</b> row. Only that row is fetched from
    /// the server — the rest of the result set is never materialised. Returns <c>default(T)</c> when
    /// the query yields no rows or the value is NULL.
    /// </summary>
    /// <typeparam name="T">
    /// The expected value type. Supported are <see cref="Nullable{T}"/> of a value type, enums
    /// (including <see cref="Nullable{T}"/> of an enum), the common PostgreSQL primitives, and any
    /// <see cref="IConvertible"/> type.
    /// </typeparam>
    Task<T> ExecuteReaderFirst<T>(string sql, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ExecuteReaderFirst{T}(string, CancellationToken)"/>
    Task<T> ExecuteReaderFirst<T>(
        string sql,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default);

    // BeginBinaryImport
    ValueTask<ulong> BeginBinaryImport(
        string sql,
        Func<NpgsqlBinaryImporter, CancellationToken, Task<ulong>> write,
        CancellationToken cancellationToken = default);
}
