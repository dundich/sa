namespace Sa.Partitional.PostgreSql.SqlBuilder;

internal class SqlBuilderFactory(ISettingsBuilder configuration) : ISqlBuilderFactory
{
    public ISqlBuilder Create() => new SqlBuilder(configuration.Build());
}
