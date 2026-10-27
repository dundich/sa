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
    /// still does not match is scanned against the full names as a last resort, and a name that
    /// arrives quoted from PostgreSQL (<c>"sc""h"."my table"</c>) is retried in its unquoted form.
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

        // Only the quotes that *enclose* each part are removed, and each part is unescaped on its own.
        // Stripping every quote character in the string - what this used to do - also rewrites names
        // that legitimately contain one, and could resolve to a different table than the caller meant.
        string? unquoted = TryUnquote(tableName);

        return unquoted is null || unquoted == tableName ? null : Find(unquoted);
    }

    /// <summary>
    /// Removes the enclosing quotes from each part of a possibly quoted qualified name
    /// (<c>"public"."my table"</c> becomes <c>public.my table</c>).
    /// </summary>
    /// <returns>The unquoted name, or <c>null</c> when it is not of that shape.</returns>
    private static string? TryUnquote(string tableName)
        => tableName.Contains('"') && SqlTemplate.TrySplitQualifiedName(tableName, out string[] parts)
            ? string.Join('.', parts)
            : null;

    static string GetFullName(string schemaName, string tableName) => $"{schemaName}.{tableName}";



    #endregion
}
