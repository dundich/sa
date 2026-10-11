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

    public string GetConnectionString() => _dataSource.ConnectionString;

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

    public async ValueTask<PgTransaction> BeginTransaction(CancellationToken cancellationToken = default)
    {
        NpgsqlConnection connection = await OpenDbConnection(cancellationToken).ConfigureAwait(false);
        try
        {
            NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            return new PgTransaction(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
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

    // ExecuteNonQuery

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

    public Task<int> ExecuteNonQuery(
        string sql,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default)
            => ExecuteNonQuery(sql, cmd => FillParams(cmd, parameters), cancellationToken);

    public Task<int> ExecuteNonQuery(string sql, CancellationToken cancellationToken = default)
        => ExecuteNonQuery(sql, initCommand: null, cancellationToken);

    // ExecuteScalar

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

    /// <summary>
    /// Executes a scalar query with no parameters.
    /// </summary>
    public Task<object?> ExecuteScalar(string sql, CancellationToken cancellationToken = default)
        => ExecuteScalar(sql, initCommand: null, cancellationToken);

    public async Task<T> ExecuteScalar<T>(
        string sql, Action<NpgsqlCommand>? initCommand, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteScalar(sql, initCommand, cancellationToken).ConfigureAwait(false);
        return (T)(result!);
    }

    /// <summary>
    /// Executes a typed scalar query.
    /// </summary>
    public async Task<T> ExecuteScalarTyped<T>(
        string sql, Action<NpgsqlCommand>? initCommand = null, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteScalar(sql, initCommand, cancellationToken).ConfigureAwait(false);
        if (result is null || result is DBNull)
            return default!;

        var targetType = typeof(T);

        // Handle value types that Npgsql returns boxed
        if (result is T typed)
            return typed;

        // Direct cast for common types that may not match 'is T' due to nullable annotations
        if (targetType == typeof(Guid) && result is Guid g)
            return (T)(object)g;
        if (targetType == typeof(DateTime) && result is DateTime dt)
            return (T)(object)dt;
        if (targetType == typeof(DateTimeOffset) && result is DateTimeOffset dto)
            return (T)(object)dto;

        // DateOnly -> DateTime conversion (PostgreSQL 'date' type maps to DateOnly in .NET 6+)
        if (targetType == typeof(DateTime) && result is DateOnly dateOnly)
            return (T)(object)dateOnly.ToDateTime(TimeOnly.MinValue);

        // Fallback: use as-cast for reference/nullable types
        if (result is T directCast)
            return directCast;

        return (T)Convert.ChangeType(result, typeof(T), null)!;
    }

    /// <summary>
    /// Executes a typed scalar query with parameters.
    /// </summary>
    public async Task<T> ExecuteScalarTyped<T>(
        string sql,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteScalar(sql, cmd => FillParams(cmd, parameters), cancellationToken).ConfigureAwait(false);
        if (result is null || result is DBNull)
            return default!;
        if (result is T typed)
            return typed;
        return (T)Convert.ChangeType(result, typeof(T), null)!;
    }

    // ExecuteReader

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

    public Task<int> ExecuteReader(
        string sql,
        Action<NpgsqlDataReader, int> read,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default)
        => ExecuteReader(sql, read, cmd => FillParams(cmd, parameters), cancellationToken);

    public Task<int> ExecuteReader(
        string sql, Action<NpgsqlDataReader, int> read, CancellationToken cancellationToken = default)
        => ExecuteReader(sql, read, initCommand: null, cancellationToken);

    // ExecuteReaderList

    public async Task<List<T>> ExecuteReaderList<T>(
        string sql, Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken = default)
    {
        List<T> list = [];
        await ExecuteReader(sql, (reader, _) => list.Add(read(reader)), cancellationToken).ConfigureAwait(false);
        return list;
    }

    public async Task<List<T>> ExecuteReaderList<T>(
        string sql,
        Func<NpgsqlDataReader, T> read,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default)
    {
        List<T> list = [];
        await ExecuteReader(sql, (reader, _) => list.Add(read(reader)), parameters, cancellationToken).ConfigureAwait(false);
        return list;
    }

    // ExecuteReaderFirst

    /// <inheritdoc cref="IPgDataSource.ExecuteReaderFirst{T}(string, CancellationToken)"/>
    public async Task<T> ExecuteReaderFirst<T>(
        string sql,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default)
    {
        // Not built on ExecuteReader: that one re-invokes its callback for every row (so "first"
        // would silently become "last") and drains the whole result set before returning. Opening
        // the connection directly — the same primitive ExecuteReader uses — lets us ask the server
        // for a single row and read it exactly once.
        await using NpgsqlConnection connection = await OpenDbConnection(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(sql, connection);
        FillParams(cmd, parameters);

        await using NpgsqlDataReader reader = await cmd
            .ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
            return default!;

        return ReadCurrent<T>(reader);
    }

    /// <inheritdoc cref="IPgDataSource.ExecuteReaderFirst{T}(string, CancellationToken)"/>
    public Task<T> ExecuteReaderFirst<T>(string sql, CancellationToken cancellationToken = default)
        => ExecuteReaderFirst<T>(sql, [], cancellationToken);

    // Reads column 0 of the current row as T. The Nullable<T> wrapper is peeled off first so the
    // value is read as its underlying type and the runtime can unbox it back into T — a boxed Int32
    // unboxes to int/int? but not to long? or an enum?, so the exact underlying type matters.
    // Enums are boxed through Enum.ToObject for the same reason.
    private static T ReadCurrent<T>(NpgsqlDataReader reader)
    {
        Type valueType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)ReadBoxed(reader, valueType)!;
    }

    private static object ReadBoxed(NpgsqlDataReader reader, Type valueType)
    {
        if (valueType.IsEnum)
            return Enum.ToObject(valueType, ReadBoxed(reader, Enum.GetUnderlyingType(valueType)));

        if (valueType == typeof(Guid))
            return reader.GetFieldValue<Guid>(0);
        if (valueType == typeof(DateTimeOffset))
            return reader.GetFieldValue<DateTimeOffset>(0);
        if (valueType == typeof(TimeSpan))
            return reader.GetFieldValue<TimeSpan>(0);
        if (valueType == typeof(DateOnly))
            return reader.GetFieldValue<DateOnly>(0);
        if (valueType == typeof(TimeOnly))
            return reader.GetFieldValue<TimeOnly>(0);

        return Type.GetTypeCode(valueType) switch
        {
            TypeCode.Char => reader.GetChar(0),
            TypeCode.String => reader.GetString(0),
            TypeCode.Boolean => reader.GetBoolean(0),
            TypeCode.Double => reader.GetDouble(0),
            TypeCode.Single => reader.GetFloat(0),
            TypeCode.Decimal => reader.GetDecimal(0),
            TypeCode.DateTime => reader.GetDateTime(0),
            TypeCode.Int64 => reader.GetInt64(0),
            TypeCode.Int32 => reader.GetInt32(0),
            TypeCode.Int16 => reader.GetInt16(0),
            TypeCode.Byte => reader.GetByte(0),
            // Everything else that is IConvertible (uint, ulong, sbyte, ...). A genuinely
            // unsupported type surfaces as the InvalidCastException Convert.ChangeType throws.
            _ => Convert.ChangeType(reader.GetValue(0), valueType, null)!,
        };
    }

    private static void FillParams(NpgsqlCommand cmd, IReadOnlyCollection<NpgsqlParameter> parameters)
    {
        foreach (var p in parameters) cmd.Parameters.Add(p);
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
