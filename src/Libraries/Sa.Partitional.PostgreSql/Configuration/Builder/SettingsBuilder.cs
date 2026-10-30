using Sa.Partitional.PostgreSql.Settings;

namespace Sa.Partitional.PostgreSql.Configuration.Builder;

internal sealed class SettingsBuilder(string? searchPath) : ISettingsBuilder
{
    private readonly Dictionary<string, ISchemaBuilder> _schemas = [];

    public string DefaultSchema { get; } = searchPath ?? "public";

    public ISettingsBuilder AddSchema(Action<ISchemaBuilder> schemaBuilder)
    {
        return AddSchema(DefaultSchema, schemaBuilder);
    }

    public ISettingsBuilder AddSchema(string schemaName, Action<ISchemaBuilder> schemaBuilder)
    {
        ArgumentNullException.ThrowIfNull(schemaBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);

        // Identifiers are stored unquoted here and quoted again when the DDL is generated, so a name
        // that arrives already quoted ("public") has to be normalised first: otherwise it opens a
        // second schema group whose table full names collide with the unquoted ones.
        schemaName = schemaName.Trim('"');

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
