using Testcontainers.PostgreSql;

namespace Sa.Data.PostgreSql.Fixture;

public record PostgreSqlFixtureSettings
{
    public string DockerImage { get; set; } = "postgres:latest";
    public Action<PostgreSqlBuilder>? Configure { get; set; }

    public readonly static PostgreSqlFixtureSettings Instance = new();
}
