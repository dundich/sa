using Npgsql;
using System.Collections.Concurrent;
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
    /// Reused command objects. A command is not tied to a connection until execution, so one
    /// instance can serve successive queries on different pooled connections — the per-query
    /// allocation was the command and its parameter collection, not the SQL itself.
    /// </summary>
    private readonly ConcurrentBag<NpgsqlCommand> _commandPool = new();
    private int _commandPoolCount;
    private const int MaxPooledCommands = 64;

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

    private NpgsqlCommand RentCommand(string sql, NpgsqlConnection connection)
    {
        NpgsqlCommand command = _commandPool.TryTake(out NpgsqlCommand? pooled)
            ? pooled
            : new NpgsqlCommand();

        command.CommandText = sql;
        command.Connection = connection;
        command.Transaction = null;
        return command;
    }

    private void ReturnCommand(NpgsqlCommand command)
    {
        // Release the per-query state before the command can be taken by another caller: the
        // parameters hold their values, and the command still points at this query's connection.
        command.Parameters.Clear();
        command.CommandText = string.Empty;
        command.Connection = null;
        command.Transaction = null;

        if (Interlocked.Increment(ref _commandPoolCount) <= MaxPooledCommands)
        {
            _commandPool.Add(command);
        }
        else
        {
            Interlocked.Decrement(ref _commandPoolCount);
            command.Dispose();
        }
    }

    private void DisposeCommands()
    {
        while (_commandPool.TryTake(out NpgsqlCommand? command))
        {
            command.Dispose();
        }

        _commandPoolCount = 0;
    }

    public void Dispose()
    {
        DisposeCommands();

        if (_ownsDataSource)
        {
            _dataSource.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        DisposeCommands();

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
        // Not pooled: initCommand is free to mutate CommandTimeout/CommandType/result-type flags,
        // and those would leak into the next renter. The parameter-only overloads did not have a
        // caller-supplied mutator, so they are the ones that reuse a command.
        await using NpgsqlConnection connection = await OpenDbConnection(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand cmd = new(sql, connection);
        initCommand?.Invoke(cmd);
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ExecuteNonQuery(
        string sql,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default)
    {
        // Direct body rather than an Action<NpgsqlCommand> lambda: this overload is used by the
        // hot query paths, and the closure was allocated on every call only to run FillParams.
        await using NpgsqlConnection connection = await OpenDbConnection(cancellationToken).ConfigureAwait(false);
        NpgsqlCommand cmd = RentCommand(sql, connection);

        try
        {
            FillParams(cmd, parameters);
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReturnCommand(cmd);
        }
    }

    public Task<int> ExecuteNonQuery(string sql, CancellationToken cancellationToken = default)
        => ExecuteNonQuery(sql, initCommand: null, cancellationToken);

    // ExecuteScalar

    public async Task<object?> ExecuteScalar(
        string sql,
        Action<NpgsqlCommand>? initCommand,
        CancellationToken cancellationToken = default)
    {
        // Not pooled — see ExecuteNonQuery(Action): the caller's mutator can leave state behind.
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
        return ConvertScalar<T>(result);
    }

    /// <summary>
    /// Executes a typed scalar query with parameters.
    /// </summary>
    public async Task<T> ExecuteScalarTyped<T>(
        string sql,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await OpenDbConnection(cancellationToken).ConfigureAwait(false);
        NpgsqlCommand cmd = RentCommand(sql, connection);

        try
        {
            FillParams(cmd, parameters);
            var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return ConvertScalar<T>(result);
        }
        finally
        {
            ReturnCommand(cmd);
        }
    }

    private static T ConvertScalar<T>(object? result)
    {
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

    // ExecuteReader

    public async Task<int> ExecuteReader(
        string sql,
        Action<NpgsqlDataReader, int> read,
        Action<NpgsqlCommand>? initCommand,
        CancellationToken cancellationToken = default)
    {
        int rowCount = 0;

        // Not pooled — see ExecuteNonQuery(Action): the caller's mutator can leave state behind.
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

    public async Task<int> ExecuteReader(
        string sql,
        Action<NpgsqlDataReader, int> read,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default)
    {
        int rowCount = 0;

        await using NpgsqlConnection connection = await OpenDbConnection(cancellationToken).ConfigureAwait(false);
        NpgsqlCommand cmd = RentCommand(sql, connection);

        try
        {
            FillParams(cmd, parameters);
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !cancellationToken.IsCancellationRequested)
            {
                read(reader, rowCount);
                rowCount++;
            }

            return rowCount;
        }
        finally
        {
            ReturnCommand(cmd);
        }
    }

    public Task<int> ExecuteReader(
        string sql, Action<NpgsqlDataReader, int> read, CancellationToken cancellationToken = default)
        => ExecuteReader(sql, read, initCommand: null, cancellationToken);

    // ExecuteReaderList

    public async Task<List<T>> ExecuteReaderList<T>(
        string sql, Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await OpenDbConnection(cancellationToken).ConfigureAwait(false);
        NpgsqlCommand cmd = RentCommand(sql, connection);

        try
        {
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            // Read the rows here instead of routing through ExecuteReader's per-row callback:
            // that callback was a closure over the list and the reader delegate, allocated per call.
            List<T> list = [];

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(read(reader));
            }

            return list;
        }
        finally
        {
            ReturnCommand(cmd);
        }
    }

    public async Task<List<T>> ExecuteReaderList<T>(
        string sql,
        Func<NpgsqlDataReader, T> read,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await OpenDbConnection(cancellationToken).ConfigureAwait(false);
        NpgsqlCommand cmd = RentCommand(sql, connection);

        try
        {
            FillParams(cmd, parameters);
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            List<T> list = [];

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(read(reader));
            }

            return list;
        }
        finally
        {
            ReturnCommand(cmd);
        }
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
        NpgsqlCommand cmd = RentCommand(sql, connection);

        try
        {
            FillParams(cmd, parameters);

            await using NpgsqlDataReader reader = await cmd
                .ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
                return default!;

            return ReadCurrent<T>(reader);
        }
        finally
        {
            ReturnCommand(cmd);
        }
    }

    /// <inheritdoc cref="IPgDataSource.ExecuteReaderFirst{T}(string, CancellationToken)"/>
    public Task<T> ExecuteReaderFirst<T>(string sql, CancellationToken cancellationToken = default)
        => ExecuteReaderFirst<T>(sql, [], cancellationToken);

    // Reads column 0 of the current row as T. The Nullable<T> wrapper is peeled off first so the
    // value is read as its underlying type and the runtime can unbox it back into T — a boxed Int32
    // unboxes to int/int? but not to long? or an enum?, so the exact underlying type matters.
    // Enums are boxed through Enum.ToObject for the same reason.
    //
    // The type dispatch is built once per closed T, not per call: ExecuteReaderFirst is a scalar
    // hot path, and Nullable.GetUnderlyingType/IsEnum/GetTypeCode were repeated on every query.
    private static T ReadCurrent<T>(NpgsqlDataReader reader) => ReadCurrentCache<T>.Reader(reader);

    private static class ReadCurrentCache<T>
    {
        public static readonly Func<NpgsqlDataReader, T> Reader = Build();

        private static Func<NpgsqlDataReader, T> Build()
        {
            Type valueType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

            if (valueType.IsEnum)
            {
                Type underlying = Enum.GetUnderlyingType(valueType);
                return reader => (T)Enum.ToObject(valueType, ReadBoxed(reader, underlying));
            }

            if (valueType == typeof(Guid))
                return reader => (T)(object)reader.GetFieldValue<Guid>(0);
            if (valueType == typeof(DateTimeOffset))
                return reader => (T)(object)reader.GetFieldValue<DateTimeOffset>(0);
            if (valueType == typeof(TimeSpan))
                return reader => (T)(object)reader.GetFieldValue<TimeSpan>(0);
            if (valueType == typeof(DateOnly))
                return reader => (T)(object)reader.GetFieldValue<DateOnly>(0);
            if (valueType == typeof(TimeOnly))
                return reader => (T)(object)reader.GetFieldValue<TimeOnly>(0);

            return Type.GetTypeCode(valueType) switch
            {
                TypeCode.Char => reader => (T)(object)reader.GetChar(0),
                TypeCode.String => reader => (T)(object)reader.GetString(0),
                TypeCode.Boolean => reader => (T)(object)reader.GetBoolean(0),
                TypeCode.Double => reader => (T)(object)reader.GetDouble(0),
                TypeCode.Single => reader => (T)(object)reader.GetFloat(0),
                TypeCode.Decimal => reader => (T)(object)reader.GetDecimal(0),
                TypeCode.DateTime => reader => (T)(object)reader.GetDateTime(0),
                TypeCode.Int64 => reader => (T)(object)reader.GetInt64(0),
                TypeCode.Int32 => reader => (T)(object)reader.GetInt32(0),
                TypeCode.Int16 => reader => (T)(object)reader.GetInt16(0),
                TypeCode.Byte => reader => (T)(object)reader.GetByte(0),
                // Everything else that is IConvertible (uint, ulong, sbyte, ...). A genuinely
                // unsupported type surfaces as the InvalidCastException Convert.ChangeType throws.
                _ => reader => (T)Convert.ChangeType(reader.GetValue(0), valueType, null)!,
            };
        }
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
