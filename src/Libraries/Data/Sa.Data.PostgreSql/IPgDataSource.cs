using Npgsql;

namespace Sa.Data.PostgreSql;

/// <summary>
/// Abstraction over a PostgreSQL data source: exposes the connection string, opens connections
/// and runs queries without leaking the underlying <see cref="NpgsqlDataSource"/> to consumers.
/// </summary>
public interface IPgDataSource : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Creates a data source that owns a connection pool built from the given connection string.
    /// </summary>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    /// <returns>A new data source that owns its pool.</returns>
    public static IPgDataSource Create(string connectionString)
        => new PgDataSource(connectionString);

    /// <summary>
    /// Wraps an existing <see cref="NpgsqlDataSource"/> without taking ownership of it.
    /// </summary>
    /// <param name="dataSource">The data source to wrap.</param>
    /// <returns>A wrapper that reuses (and never disposes) the given pool.</returns>
    public static IPgDataSource Create(NpgsqlDataSource dataSource)
        => new PgDataSource(dataSource);

    /// <summary>
    /// Returns the search path of the underlying data source.
    /// </summary>
    string GetSearchPath();

    /// <summary>
    /// Returns the connection string of the underlying data source.
    /// </summary>
    string GetConnectionString();

    /// <summary>
    /// Opens a new connection from the pool.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The opened connection; the caller owns and must dispose it.</returns>
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

    /// <summary>
    /// Executes a non-query statement, letting the caller configure the command first.
    /// </summary>
    /// <param name="sql">The SQL statement to execute.</param>
    /// <param name="initCommand">An optional callback to configure the command (parameters, timeout, ...).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The number of rows affected.</returns>
    Task<int> ExecuteNonQuery(
        string sql,
        Action<NpgsqlCommand>? initCommand,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a non-query statement with the given parameters.
    /// </summary>
    /// <param name="sql">The SQL statement to execute.</param>
    /// <param name="parameters">The parameters to bind.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The number of rows affected.</returns>
    Task<int> ExecuteNonQuery(
        string sql,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a non-query statement with no parameters.
    /// </summary>
    /// <param name="sql">The SQL statement to execute.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The number of rows affected.</returns>
    Task<int> ExecuteNonQuery(string sql, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a scalar query, letting the caller configure the command first.
    /// </summary>
    /// <param name="sql">The SQL query to execute.</param>
    /// <param name="initCommand">An optional callback to configure the command (parameters, timeout, ...).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The first column of the first row, or <c>null</c>.</returns>
    Task<object?> ExecuteScalar(
        string sql, Action<NpgsqlCommand>? initCommand, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a scalar query with no parameters.
    /// </summary>
    Task<object?> ExecuteScalar(string sql, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a typed scalar query, letting the caller configure the command first.
    /// </summary>
    /// <typeparam name="T">The expected result type.</typeparam>
    /// <param name="sql">The SQL query to execute.</param>
    /// <param name="initCommand">An optional callback to configure the command (parameters, timeout, ...).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The first column of the first row converted to <typeparamref name="T"/>.</returns>
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
    /// <summary>
    /// Executes a reader query, letting the caller configure the command first.
    /// </summary>
    /// <param name="sql">The SQL query to execute.</param>
    /// <param name="read">A callback invoked for every row with the reader and the row index.</param>
    /// <param name="initCommand">An optional callback to configure the command (parameters, timeout, ...).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The number of rows read.</returns>
    Task<int> ExecuteReader(
        string sql,
        Action<NpgsqlDataReader, int> read,
        Action<NpgsqlCommand>? initCommand,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a reader query with the given parameters.
    /// </summary>
    /// <param name="sql">The SQL query to execute.</param>
    /// <param name="read">A callback invoked for every row with the reader and the row index.</param>
    /// <param name="parameters">The parameters to bind.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The number of rows read.</returns>
    Task<int> ExecuteReader(
        string sql,
        Action<NpgsqlDataReader, int> read,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a reader query with no parameters.
    /// </summary>
    /// <param name="sql">The SQL query to execute.</param>
    /// <param name="read">A callback invoked for every row with the reader and the row index.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The number of rows read.</returns>
    Task<int> ExecuteReader(
        string sql, Action<NpgsqlDataReader, int> read, CancellationToken cancellationToken = default);

    // ExecuteReaderList

    /// <summary>
    /// Executes a query and materialises every row as a list item.
    /// </summary>
    /// <typeparam name="T">The item type produced for each row.</typeparam>
    /// <param name="sql">The SQL query to execute.</param>
    /// <param name="read">A callback that maps the current row to an item.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The materialised rows.</returns>
    Task<List<T>> ExecuteReaderList<T>(
        string sql, Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a query with the given parameters and materialises every row as a list item.
    /// </summary>
    /// <typeparam name="T">The item type produced for each row.</typeparam>
    /// <param name="sql">The SQL query to execute.</param>
    /// <param name="read">A callback that maps the current row to an item.</param>
    /// <param name="parameters">The parameters to bind.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The materialised rows.</returns>
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

    /// <summary>
    /// Starts a binary import on a dedicated connection.
    /// </summary>
    /// <param name="sql">The COPY statement to execute.</param>
    /// <param name="write">A callback that writes rows to the importer and returns the number of rows written.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The number of rows written.</returns>
    ValueTask<ulong> BeginBinaryImport(
        string sql,
        Func<NpgsqlBinaryImporter, CancellationToken, Task<ulong>> write,
        CancellationToken cancellationToken = default);
}
