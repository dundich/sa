using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql.SqlBuilder;

internal sealed class SqlBuilder : ISqlBuilder
{
    private readonly Dictionary<string, SqlTableBuilder> builders;

    /// <summary>
    /// Resolved lookup: full name and the bare name of a table in the default schema.
    /// <see cref="Find"/> used to qualify an unqualified name by interpolating
    /// <c>schema.table</c> on every call (one string allocation per EnsureParts), then fall back
    /// to a linear scan. Both are now a single dictionary lookup; the quoted-name path below is
    /// the only remaining fallback.
    /// </summary>
    private readonly Dictionary<string, SqlTableBuilder> _byName;

    public SqlBuilder(ITableSettingsStorage storage)
    {
        builders = storage
            .Tables
            .Select(table => new SqlTableBuilder(table))
            .ToDictionary(c => c.FullName, StringComparer.Ordinal);

        string? defaultSchema = storage.Schemas.Count > 0 ? storage.Schemas.First() : null;

        _byName = new Dictionary<string, SqlTableBuilder>(builders.Count * 2, StringComparer.Ordinal);

        foreach (SqlTableBuilder builder in builders.Values)
        {
            _byName[builder.FullName] = builder;

            // Only the default schema answers an unqualified name, matching the old lookup.
            if (string.Equals(builder.Settings.DatabaseSchemaName, defaultSchema, StringComparison.Ordinal))
            {
                _byName[builder.Settings.DatabaseTableName] = builder;
            }
        }
    }


    public IReadOnlyCollection<ISqlTableBuilder> Tables => builders.Values;

    public async IAsyncEnumerable<string> MigrateSql(DateTimeOffset[] dates, Func<string, Task<StrOrNum[][]>> resolve)
    {
        foreach (string table in builders.Keys)
        {
            SqlTableBuilder builder = builders[table];

            StrOrNum[][] parValues = await resolve(table).ConfigureAwait(false);

            if (parValues.Length == 0 && builder.Settings.PartByListFieldNames.Length > 0)
            {
                // A list-partitioned table needs one value per list level; with none resolved there is
                // no leaf to create. This used to emit the root and the list levels only, producing a
                // tree whose deepest level had no range leaf - every later INSERT then failed with
                // "no partition of relation ... found for row". Nothing to do is the honest answer.
                continue;
            }

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


    public string SelectPartsFromDateSql(string tableName)
        => (Find(tableName) ?? throw new KeyNotFoundException(tableName)).SelectPartsFromDate;

    public string SelectPartsToDateSql(string tableName)
        => (Find(tableName) ?? throw new KeyNotFoundException(tableName)).SelectPartsToDate;

    #region privates
    /// <summary>
    /// Resolves a table name to its builder.
    /// </summary>
    /// <remarks>
    /// A name that is already qualified (<c>schema.table</c>) is looked up as is. An unqualified name
    /// resolves to the table of that name in the <b>first</b> configured schema, so with several
    /// schemas declared the resolution depends on declaration order - pass a qualified name when that
    /// matters. A name that arrives quoted from PostgreSQL (<c>"sc""h"."my table"</c>) is retried in
    /// its unquoted form.
    /// </remarks>
    private ISqlTableBuilder? Find(string tableName)
    {
        if (_byName.TryGetValue(tableName, out SqlTableBuilder? item)) return item;

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

    #endregion
}
