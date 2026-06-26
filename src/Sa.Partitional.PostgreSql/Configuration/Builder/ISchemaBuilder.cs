namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Builder for declaring one or more database schemas and the tables they contain.
/// </summary>
public interface ISchemaBuilder
{
    /// <summary>
    /// Creates a new table definition builder using the schema's default column set (an auto-generated <c>id</c> column).
    /// </summary>
    /// <param name="tableName">The logical table name (without schema prefix).</param>
    /// <returns>A fluent <see cref="ITableBuilder"/> for further configuration.</returns>
    ITableBuilder CreateTable(string tableName);

    /// <summary>
    /// Creates a new table definition builder with explicitly provided SQL field definitions.
    /// </summary>
    /// <param name="tableName">The logical table name (without schema prefix).</param>
    /// <param name="sqlFields">Raw SQL column definitions, e.g. <c>"created_at timestamptz NOT NULL"</c>.</param>
    /// <returns>A fluent <see cref="ITableBuilder"/> for further configuration.</returns>
    ITableBuilder AddTable(string tableName, params string[] sqlFields);

    /// <summary>
    /// Validates and materialises all configured tables into <see cref="ITableSettings"/> instances.
    /// </summary>
    /// <returns>An array of immutable table settings ready for runtime use.</returns>
    ITableSettings[] Build();
}
