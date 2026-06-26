using Npgsql;
using System.Data;

namespace Sa.Data.PostgreSql;

/// <summary>
/// NpgsqlDataSource lite
/// </summary>
/// <param name="settings">connection string</param>
internal sealed class PgDataSource(PgDataSourceSettings settings) : IPgDataSource
{
    private readonly Lazy<NpgsqlDataSource> _dataSource
        = new(() => NpgsqlDataSource.Create(settings.ConnectionString));

    public string GetSearchPath() => settings.GetSearchPath();

    public ValueTask<NpgsqlConnection> OpenDbConnection(CancellationToken cancellationToken)
        => _dataSource.Value.OpenConnectionAsync(cancellationToken);

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

    public async ValueTask<ulong> BeginBinaryImport(
        string sql,
        Func<NpgsqlBinaryImporter, CancellationToken, Task<ulong>> write,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection db = await OpenDbConnection(cancellationToken);
        await using NpgsqlBinaryImporter writer = await db.BeginBinaryImportAsync(sql, cancellationToken);
        ulong result = await write(writer, cancellationToken);
        return result;
    }

    public async Task ExecuteTransactionAsync(
        Func<NpgsqlTransaction, CancellationToken, Task> action,
        IsolationLevel isolationLevel = IsolationLevel.Unspecified,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await OpenDbConnection(cancellationToken);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
        try
        {
            await action(transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<int> ExecuteNonQuery(
        string sql,
        Action<NpgsqlCommand>? initCommand,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await OpenDbConnection(cancellationToken);
        await using NpgsqlCommand cmd = new(sql, connection);
        initCommand?.Invoke(cmd);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<object?> ExecuteScalar(
        string sql,
        Action<NpgsqlCommand>? initCommand,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await OpenDbConnection(cancellationToken);
        await using NpgsqlCommand cmd = new(sql, connection);
        initCommand?.Invoke(cmd);
        return await cmd.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<int> ExecuteReader(
        string sql,
        Action<NpgsqlDataReader, int> read,
        Action<NpgsqlCommand>? initCommand,
        CancellationToken cancellationToken = default)
    {
        int rowCount = 0;

        using NpgsqlConnection connection = await OpenDbConnection(cancellationToken);
        await using NpgsqlCommand cmd = new(sql, connection);
        initCommand?.Invoke(cmd);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken) && !cancellationToken.IsCancellationRequested)
        {
            read(reader, rowCount);
            rowCount++;
        }
        return rowCount;
    }
}
