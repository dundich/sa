using Npgsql;

namespace Sa.Data.PostgreSql;

public interface IPgDataSource : IDisposable, IAsyncDisposable
{
    public static IPgDataSource Create(string connectionString)
        => new PgDataSource(new PgDataSourceSettings(connectionString));

    string GetSearchPath();

    ValueTask<NpgsqlConnection> OpenDbConnection(CancellationToken cancellationToken);

    Task<int> ExecuteNonQuery(
        string sql,
        Action<NpgsqlCommand>? initCommand,
        CancellationToken cancellationToken = default);

    Task<int> ExecuteNonQuery(
        string sql,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default)
            => ExecuteNonQuery(sql, cmd => FillParams(cmd, parameters), cancellationToken);

    Task<int> ExecuteNonQuery(string sql, CancellationToken cancellationToken = default)
        => ExecuteNonQuery(sql, [], cancellationToken);

    Task<object?> ExecuteScalar(
        string sql, Action<NpgsqlCommand>? initCommand, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a scalar query with no parameters.
    /// </summary>
    Task<object?> ExecuteScalar(string sql, CancellationToken cancellationToken = default)
        => ExecuteScalar(sql, null, cancellationToken);


    async Task<T> ExecuteScalar<T>(
        string sql, Action<NpgsqlCommand>? initCommand, CancellationToken cancellationToken = default)
            => ((T)(await ExecuteScalar(sql, initCommand, cancellationToken))!);


    /// <summary>
    /// Executes a typed scalar query.
    /// </summary>
    async Task<T> ExecuteScalarTyped<T>(
        string sql, Action<NpgsqlCommand>? initCommand = null, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteScalar(sql, initCommand, cancellationToken);
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
    async Task<T> ExecuteScalarTyped<T>(
        string sql,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteScalar(sql, cmd => FillParams(cmd, parameters), cancellationToken);
        if (result is null || result is DBNull)
            return default!;
        if (result is T typed)
            return typed;
        return (T)Convert.ChangeType(result, typeof(T), null)!;
    }


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
        CancellationToken cancellationToken = default)
        => ExecuteReader(sql, read, cmd => FillParams(cmd, parameters), cancellationToken);

    async Task<int> ExecuteReader(
        string sql, Action<NpgsqlDataReader, int> read, CancellationToken cancellationToken = default)
        => await ExecuteReader(sql, read, [], cancellationToken);


    // ExecuteReaderList


    async Task<List<T>> ExecuteReaderList<T>(
        string sql, Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken = default)
    {
        List<T> list = [];
        await ExecuteReader(sql, (reader, _) => list.Add(read(reader)), cancellationToken);
        return list;
    }

    async Task<List<T>> ExecuteReaderList<T>(
        string sql,
        Func<NpgsqlDataReader, T> read,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default)
    {
        List<T> list = [];
        await ExecuteReader(sql, (reader, _) => list.Add(read(reader)), parameters, cancellationToken);
        return list;
    }


    // ExecuteReaderFirst


    Task<T> ExecuteReaderFirst<T>(string sql, CancellationToken cancellationToken = default)
    {
        return ExecuteReaderFirst<T>(sql, [], cancellationToken);
    }

    async Task<T> ExecuteReaderFirst<T>(
        string sql,
        IReadOnlyCollection<NpgsqlParameter> parameters,
        CancellationToken cancellationToken = default)
    {
        T value = default!;

        await ExecuteReader(sql, (reader, _) =>
        {
            value = typeof(T) switch
            {
                Type t when t == typeof(Guid) => (T)(object)reader.GetFieldValue<Guid>(0),
                Type t when t == typeof(DateTimeOffset) => (T)(object)reader.GetFieldValue<DateTimeOffset>(0),
                _ => Type.GetTypeCode(typeof(T)) switch
                {
                    TypeCode.Char => (T)(object)reader.GetChar(0),
                    TypeCode.Int64 => (T)(object)reader.GetInt64(0),
                    TypeCode.Int32 => (T)(object)reader.GetInt32(0),
                    TypeCode.String => (T)(object)reader.GetString(0),
                    TypeCode.Boolean => (T)(object)reader.GetBoolean(0),
                    TypeCode.Double => (T)(object)reader.GetDouble(0),
                    TypeCode.DateTime => (T)(object)reader.GetDateTime(0),
                    TypeCode.Decimal => (T)(object)reader.GetDecimal(0),
                    TypeCode.Int16 => (T)(object)reader.GetInt16(0),
                    TypeCode.DBNull => value,
                    _ => throw new InvalidOperationException($"Unsupported type: {typeof(T)}"),
                }
            };
        }
        , parameters
        , cancellationToken);

        return value;
    }


    // BeginBinaryImport
    ValueTask<ulong> BeginBinaryImport(
        string sql,
        Func<NpgsqlBinaryImporter, CancellationToken, Task<ulong>> write,
        CancellationToken cancellationToken = default);

    void FillParams(NpgsqlCommand cmd, IReadOnlyCollection<NpgsqlParameter> parameters)
    {
        foreach (var p in parameters) cmd.Parameters.Add(p);
    }
}
