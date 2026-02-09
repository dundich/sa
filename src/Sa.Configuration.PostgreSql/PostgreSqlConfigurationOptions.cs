namespace Sa.Configuration.PostgreSql;

using Npgsql;

public sealed record PostgreSqlConfigurationOptions(
    string ConnectionString,
    string SelectSql,
    params IReadOnlyCollection<NpgsqlParameter> Parameters);
