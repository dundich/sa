using Npgsql;

namespace Sa.Data.PostgreSql;

public interface IPgDataSource
{
    public static IPgDataSource Create(string connectionString) => new PgDataSource(new PgDataSourceSettings(connectionString));


    // ExecuteNonQuery

    Task<int> ExecuteNonQuery(string sql, IReadOnlyCollection<NpgsqlParameter> parameters, CancellationToken cancellationToken = default);

    async Task<int> ExecuteNonQuery(string sql, CancellationToken cancellationToken = default)
        => await ExecuteNonQuery(sql, [], cancellationToken);

    Task<object?> ExecuteScalar(string sql, IReadOnlyCollection<NpgsqlParameter> parameters, CancellationToken cancellationToken = default);

    // ExecuteReader

    Task<int> ExecuteReader(string sql, Action<NpgsqlDataReader, int> read, IReadOnlyCollection<NpgsqlParameter> parameters, CancellationToken cancellationToken = default);

    async Task<int> ExecuteReader(string sql, Action<NpgsqlDataReader, int> read, CancellationToken cancellationToken = default)
        => await ExecuteReader(sql, read, [], cancellationToken);


    // ExecuteReaderList


    async Task<List<T>> ExecuteReaderList<T>(string sql, Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken = default)
    {
        List<T> list = [];
        await ExecuteReader(sql, (reader, _) => list.Add(read(reader)), cancellationToken);
        return list;
    }

    async Task<List<T>> ExecuteReaderList<T>(string sql, Func<NpgsqlDataReader, T> read, IReadOnlyCollection<NpgsqlParameter> parameters, CancellationToken cancellationToken = default)
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

    async Task<T> ExecuteReaderFirst<T>(string sql, IReadOnlyCollection<NpgsqlParameter> parameters, CancellationToken cancellationToken = default)
    {
        T value = default!;

        await ExecuteReader(sql, (reader, _) =>
        {
            value = Type.GetTypeCode(typeof(T)) switch
            {
                TypeCode.Char => (T)(object)reader.GetChar(0),
                TypeCode.Int64 => (T)(object)reader.GetInt64(0),
                TypeCode.Int32 => (T)(object)reader.GetInt32(0),
                TypeCode.String => (T)(object)reader.GetString(0),
                TypeCode.Boolean => (T)(object)reader.GetBoolean(0),
                TypeCode.Double => (T)(object)reader.GetDouble(0),
                TypeCode.DateTime => (T)(object)reader.GetDateTime(0),
                TypeCode.Decimal => (T)(object)reader.GetDecimal(0),
                TypeCode.DBNull => value,
                _ => throw new InvalidOperationException($"Unsupported type: {typeof(T)}"),
            };
        }
        , parameters
        , cancellationToken);

        return value;
    }


    // BeginBinaryImport


    ValueTask<ulong> BeginBinaryImport(string sql, Func<NpgsqlBinaryImporter, CancellationToken, Task<ulong>> write, CancellationToken cancellationToken = default);
}
