using Npgsql;
using System.Data;

namespace Sa.Data.PostgreSql;

/// <summary>
/// NpgsqlDataSource lite
/// </summary>
internal sealed class PgDataSource : IPgDataSource
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _searchPath;
    private readonly bool _ownsDataSource;

    /// <summary>
    /// Строит собственный <see cref="NpgsqlDataSource"/> из строки подключения и владеет им
    /// (dispose его). Так собирается data source, когда задана <see cref="PgDataSourceOptions.ConnectionString"/>.
    /// </summary>
    /// <param name="connectionString">строка подключения</param>
    public PgDataSource(string connectionString)
        : this(NpgsqlDataSource.Create(connectionString), SearchPathOf(connectionString), ownsDataSource: true)
    {
    }

    /// <summary>
    /// Переиспользует уже зарегистрированный <see cref="NpgsqlDataSource"/> (общий pool) и НЕ
    /// dispose его — владелец им является тот, кто его зарегистрировал.
    /// </summary>
    public PgDataSource(NpgsqlDataSource dataSource)
        : this(dataSource, SearchPathOf(dataSource.ConnectionString), ownsDataSource: false)
    {
    }

    private PgDataSource(NpgsqlDataSource dataSource, string searchPath, bool ownsDataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _searchPath = searchPath;
        _ownsDataSource = ownsDataSource;
    }

    public string GetSearchPath() => _searchPath;

    public ValueTask<NpgsqlConnection> OpenDbConnection(CancellationToken cancellationToken)
        => _dataSource.OpenConnectionAsync(cancellationToken);

    public void Dispose()
    {
        if (_ownsDataSource)
        {
            _dataSource.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask<ulong> BeginBinaryImport(
        string sql,
        Func<NpgsqlBinaryImporter, CancellationToken, Task<ulong>> write,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection db = await OpenDbConnection(cancellationToken).ConfigureAwait(false);
        await using NpgsqlBinaryImporter writer = await db.BeginBinaryImportAsync(sql, cancellationToken).ConfigureAwait(false);
        ulong result = await write(writer, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task ExecuteTransactionAsync(
        Func<NpgsqlTransaction, CancellationToken, Task> action,
        IsolationLevel isolationLevel = IsolationLevel.Unspecified,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await OpenDbConnection(cancellationToken).ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        try
        {
            await action(transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<int> ExecuteNonQuery(
        string sql,
        Action<NpgsqlCommand>? initCommand,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await OpenDbConnection(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(sql, connection);
        initCommand?.Invoke(cmd);
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<object?> ExecuteScalar(
        string sql,
        Action<NpgsqlCommand>? initCommand,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await OpenDbConnection(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(sql, connection);
        initCommand?.Invoke(cmd);
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ExecuteReader(
        string sql,
        Action<NpgsqlDataReader, int> read,
        Action<NpgsqlCommand>? initCommand,
        CancellationToken cancellationToken = default)
    {
        int rowCount = 0;

        await using NpgsqlConnection connection = await OpenDbConnection(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(sql, connection);
        initCommand?.Invoke(cmd);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !cancellationToken.IsCancellationRequested)
        {
            read(reader, rowCount);
            rowCount++;
        }
        return rowCount;
    }

    // Строка подключения → search path. Перенесено из удалённого PgDataSourceSettings.GetSearchPath();
    // возвращает всю (возможно, списокную) Search Path, а не только первую запись — разбиение на первую
    // делает потребитель (PostgresFileStorageSchema). Не удалось разобрать → "public".
    private static string SearchPathOf(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return builder.SearchPath ?? "public";
        }
        catch
        {
            return "public";
        }
    }
}
