namespace Sa.Partitional.PostgreSql.Configuration.Builder;

internal sealed class SchemaBuilder(string schemaName) : ISchemaBuilder
{
    public readonly Dictionary<string, TableBuilder> _tables = [];

    public ITableBuilder CreateTable(string tableName)
    {
        if (_tables.TryGetValue(tableName, out TableBuilder? table)) return table;
        TableBuilder builder = new(schemaName, tableName);
        _tables[tableName] = builder;
        return builder;
    }

    public ITableBuilder AddTable(string tableName, params string[] sqlFields)
        => CreateTable(tableName).AddFields(sqlFields);

    public ITableSettings[] Build() => [.. _tables.Values.Select(c => c.Build())];
}
