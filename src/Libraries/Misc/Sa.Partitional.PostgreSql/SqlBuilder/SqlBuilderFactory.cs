namespace Sa.Partitional.PostgreSql.SqlBuilder;

internal sealed class SqlBuilderFactory(ISettingsBuilder configuration) : ISqlBuilderFactory
{
    public ISqlBuilder Create() => new SqlBuilder(configuration.Build());
}
