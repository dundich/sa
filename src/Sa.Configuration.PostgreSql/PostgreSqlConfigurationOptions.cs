namespace Sa.Configuration.PostgreSql;

using Npgsql;

public record PostgreSqlConfigurationOptions(
    string ConnectionString,
    string SelectSql,
    params IReadOnlyCollection<NpgsqlParameter> Parameters);
