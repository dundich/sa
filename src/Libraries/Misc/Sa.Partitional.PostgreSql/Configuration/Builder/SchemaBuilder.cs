namespace Sa.Partitional.PostgreSql.Configuration.Builder;

internal sealed class SchemaBuilder(string schemaName) : ISchemaBuilder
{
    public readonly Dictionary<string, TableBuilder> _tables = [];

    /// <summary>
    /// Returns the builder of <paramref name="tableName"/>, creating it on first use.
    /// </summary>
    /// <remarks>
    /// The name is stored unquoted, exactly like <c>SettingsBuilder.AddSchema</c> stores the schema
    /// and <see cref="TableBuilder.Build"/> stores the root table name. Without that, the same table
    /// declared once as <c>"orders"</c> and once as <c>orders</c> produced two builders whose
    /// <c>FullName</c> is identical, and the SQL builder failed on the duplicate dictionary key with
    /// an error that named neither declaration.
    /// </remarks>
    public ITableBuilder CreateTable(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        string name = tableName.Trim('"');

        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_tables.TryGetValue(name, out TableBuilder? table)) return table;

        TableBuilder builder = new(schemaName, name);
        _tables[name] = builder;
        return builder;
    }

    public ITableBuilder AddTable(string tableName, params string[] sqlFields)
        => CreateTable(tableName).AddFields(sqlFields);

    public ITableBuilder AdoptTable(string tableName, params string[] sqlFields)
    {
        ArgumentNullException.ThrowIfNull(sqlFields);

        TableBuilder builder = (TableBuilder)CreateTable(tableName);
        builder.DeclareAdopt(sqlFields);
        return builder;
    }

    public ITableSettings[] Build() => [.. _tables.Values.Select(c => c.Build())];
}
