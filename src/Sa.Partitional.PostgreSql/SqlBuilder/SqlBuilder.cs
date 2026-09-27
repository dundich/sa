using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql.SqlBuilder;

internal sealed class SqlBuilder(ITableSettingsStorage storage) : ISqlBuilder
{
    private readonly Dictionary<string, SqlTableBuilder> builders = storage
        .Tables
        .Select(table => new SqlTableBuilder(table))
        .ToDictionary(c => c.FullName);


    public IReadOnlyCollection<ISqlTableBuilder> Tables => builders.Values;

    public async IAsyncEnumerable<string> MigrateSql(DateTimeOffset[] dates, Func<string, Task<StrOrNum[][]>> resolve)
    {
        foreach (string table in builders.Keys)
        {
            SqlTableBuilder builder = builders[table];

            StrOrNum[][] parValues = await resolve(table).ConfigureAwait(false);

            if (parValues.Length > 0)
            {
                foreach (StrOrNum[] parts in parValues)
                {
                    foreach (DateTimeOffset date in dates)
                    {
                        string sql = builder.CreateSql(date, parts);
                        yield return sql;
                    }
                }
            }
            else
            {
                foreach (DateTimeOffset date in dates)
                {
                    string sql = builder.CreateSql(date);
                    yield return sql;
                }
            }
        }
    }

    public ISqlTableBuilder? this[string tableName] => Find(tableName ?? throw new ArgumentNullException(nameof(tableName)));


    public string SelectPartsQualifiedTablesSql(string tableName, StrOrNum[] partValues)
        => (Find(tableName) ?? throw new KeyNotFoundException(tableName)).GetPartsSql(partValues);

    public string SelectPartsQualifiedTablesSql(string qualifiedTablesSql)
        => SqlTemplate.SelectPartsQualifiedTablesSql(qualifiedTablesSql);

    public string SelectPartsFromDateSql(string tableName)
        => (Find(tableName) ?? throw new KeyNotFoundException(tableName)).SelectPartsFromDate;

    public string SelectPartsToDateSql(string tableName)
        => (Find(tableName) ?? throw new KeyNotFoundException(tableName)).SelectPartsToDate;

    public string CreatePartSql(string tableName, DateTimeOffset date, StrOrNum[] partValues)
        => (Find(tableName) ?? throw new KeyNotFoundException(tableName)).CreateSql(date, partValues);

    #region privates
    /// <summary>
    /// Resolves a table name to its builder.
    /// </summary>
    /// <remarks>
    /// A name that is already qualified (<c>schema.table</c>) is looked up as is. An unqualified name
    /// is qualified with the <b>first</b> configured schema, so with several schemas declared the
    /// resolution depends on declaration order - pass a qualified name when that matters. A name that
    /// still does not match is scanned against the full names as a last resort, and a quoted name is
    /// retried without its quotes.
    /// </remarks>
    private ISqlTableBuilder? Find(string tableName)
    {
        // An unqualified name is resolved against the first configured schema. With no schema
        // configured at all there is nothing to qualify with - fall through to the full-name scan
        // instead of throwing on Schemas.First().
        string? defaultSchema = storage.Schemas.Count > 0 ? storage.Schemas.First() : null;

        ISqlTableBuilder? item = tableName.Contains('.') || defaultSchema is null
            ? builders.GetValueOrDefault(tableName)
            : builders.GetValueOrDefault(GetFullName(defaultSchema, tableName));

        item ??= builders.Values.FirstOrDefault(c => c.FullName == tableName);

        if (item != null) return item;

        if (tableName.Contains('"'))
        {
            tableName = tableName.Replace("\"", "");
            return Find(tableName);
        }
        return item;
    }

    static string GetFullName(string schemaName, string tableName) => $"{schemaName}.{tableName}";



    #endregion
}
