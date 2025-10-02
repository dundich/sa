using Sa.Partitional.PostgreSql.Settings;

namespace Sa.Partitional.PostgreSql.Configuration.Builder;

internal sealed class SettingsBuilder : ISettingsBuilder
{
    private readonly Dictionary<string, ISchemaBuilder> _schemas = [];

    public ISettingsBuilder AddSchema(Action<ISchemaBuilder> schemaBuilder)
    {
        return AddSchema("public", schemaBuilder);
    }

    public ISettingsBuilder AddSchema(string schemaName, Action<ISchemaBuilder> schemaBuilder)
    {
        if (!_schemas.TryGetValue(schemaName, out ISchemaBuilder? builder))
        {
            builder = new SchemaBuilder(schemaName);
            _schemas[schemaName] = builder;
        }

        schemaBuilder.Invoke(builder);
        return this;
    }

    public ITableSettingsStorage Build()
    {
        ITableSettings[] tables = [.. _schemas.Values.SelectMany(c => c.Build())];
        return new TableSettingsStorage(tables);
    }
}
