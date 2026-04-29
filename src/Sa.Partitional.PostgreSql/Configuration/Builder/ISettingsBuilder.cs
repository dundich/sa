namespace Sa.Partitional.PostgreSql;


public interface ISettingsBuilder
{
    string DefaultSchema { get; }

    ISettingsBuilder AddSchema(Action<ISchemaBuilder> schemaBuilder);
    ISettingsBuilder AddSchema(string schemaName, Action<ISchemaBuilder> schemaBuilder);
    ITableSettingsStorage Build();
}
