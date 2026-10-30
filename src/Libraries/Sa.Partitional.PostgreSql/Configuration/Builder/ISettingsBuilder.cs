namespace Sa.Partitional.PostgreSql;


/// <summary>
/// Top-level builder for partitioned-table configuration. Allows declaring schemas (default + named) and their tables.
/// </summary>
public interface ISettingsBuilder
{
    /// <summary>
    /// Gets the name of the default schema that is applied to tables without an explicit schema.
    /// </summary>
    string DefaultSchema { get; }

    /// <summary>
    /// Adds a schema using the default column set (<c>id bigint PRIMARY KEY</c>).
    /// </summary>
    /// <param name="schemaBuilder">An action that configures tables within this schema.</param>
    /// <returns>The same <see cref="ISettingsBuilder"/> for chaining.</returns>
    ISettingsBuilder AddSchema(Action<ISchemaBuilder> schemaBuilder);

    /// <summary>
    /// Adds a named schema using the default column set.
    /// </summary>
    /// <param name="schemaName">The PostgreSQL schema name.</param>
    /// <param name="schemaBuilder">An action that configures tables within this schema.</param>
    /// <returns>The same <see cref="ISettingsBuilder"/> for chaining.</returns>
    ISettingsBuilder AddSchema(string schemaName, Action<ISchemaBuilder> schemaBuilder);

    /// <summary>
    /// Validates and materialises all configured schemas and tables into an immutable <see cref="ITableSettingsStorage"/>.
    /// </summary>
    /// <returns>The settings storage ready for registration in the DI container.</returns>
    ITableSettingsStorage Build();
}
