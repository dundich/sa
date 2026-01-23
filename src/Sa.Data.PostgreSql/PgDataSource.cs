using Npgsql;

namespace Sa.Data.PostgreSql;

/// <summary>
/// NpgsqlDataSource lite
/// </summary>
/// <param name="settings">connection string</param>
public sealed class PgDataSource(PgDataSourceSettings settings) : IPgDataSource, IDisposable, IAsyncDisposable
{
    private readonly Lazy<NpgsqlDataSource> _dataSource = new(() => NpgsqlDataSource.Create(settings.ConnectionString));

    public ValueTask<NpgsqlConnection> OpenDbConnection(CancellationToken cancellationToken) => _dataSource.Value.OpenConnectionAsync(cancellationToken);

    public void Dispose()
    {
        if (_dataSource.IsValueCreated)
        {
            _dataSource.Value.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource.IsValueCreated)
        {
            await _dataSource.Value.DisposeAsync();
        }
    }

    public async ValueTask<ulong> BeginBinaryImport(string sql, Func<NpgsqlBinaryImporter, CancellationToken, Task<ulong>> write, CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection db = await OpenDbConnection(cancellationToken);
        using NpgsqlBinaryImporter writer = await db.BeginBinaryImportAsync(sql, cancellationToken);
        ulong result = await write(writer, cancellationToken);
        return result;
    }

    public async Task<int> ExecuteNonQuery(string sql, Action<NpgsqlCommand>? initCommand, CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection connection = await OpenDbConnection(cancellationToken);
        using NpgsqlCommand cmd = new(sql, connection);
        initCommand?.Invoke(cmd);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<object?> ExecuteScalar(string sql, Action<NpgsqlCommand>? initCommand, CancellationToken cancellationToken = default)
    {
        using NpgsqlConnection connection = await OpenDbConnection(cancellationToken);
        using NpgsqlCommand cmd = new(sql, connection);
        initCommand?.Invoke(cmd);
        return await cmd.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<int> ExecuteReader(string sql, Action<NpgsqlDataReader, int> read, Action<NpgsqlCommand>? initCommand, CancellationToken cancellationToken = default)
    {
        int rowCount = 0;

        using NpgsqlConnection connection = await OpenDbConnection(cancellationToken);
        using NpgsqlCommand cmd = new(sql, connection);
        initCommand?.Invoke(cmd);
        using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken) && !cancellationToken.IsCancellationRequested)
        {
            read(reader, rowCount);
            rowCount++;
        }
        return rowCount;
    }
}
