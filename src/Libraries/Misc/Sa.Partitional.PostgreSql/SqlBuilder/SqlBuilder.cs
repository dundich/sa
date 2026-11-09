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

    public async IAsyncEnumerable<MigrateBatch> MigrateSql(DateTimeOffset[] dates, Func<string, Task<StrOrNum[][]>> resolve)
    {
        foreach (string table in builders.Keys)
        {
            SqlTableBuilder builder = builders[table];

            // Table by table, so a failure of one table's migration (a resolution failure, a
            // partition name over the identifier limit, a rejected value count) is reported as a
            // batch of its own instead of unwinding the whole run before the other tables start.
            await using IAsyncEnumerator<MigrateBatch> batches =
                TableBatches(builder, dates, resolve).GetAsyncEnumerator();

            while (true)
            {
                MigrateBatch? next = null;
                Exception? error = null;

                try
                {
                    if (!await batches.MoveNextAsync().ConfigureAwait(false)) break;

                    next = batches.Current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = ex;
                }

                if (error is not null)
                {
                    yield return new MigrateBatch(builder.FullName, null, error);
                    break;
                }

                yield return next.GetValueOrDefault();
            }
        }
    }

    /// <summary>
    /// The statement batches of one table, in execution order.
    /// </summary>
    /// <remarks>
    /// The first batch carries the schema/root/cache-table DDL, the rest are leaf-only. Re-emitting
    /// the cache table's <c>ALTER TABLE … ADD COLUMN</c> per date and per list value took an
    /// <c>ACCESS EXCLUSIVE</c> lock on it hundreds of times per tick for a table with many values.
    /// </remarks>
    private static async IAsyncEnumerable<MigrateBatch> TableBatches(
        SqlTableBuilder builder,
        DateTimeOffset[] dates,
        Func<string, Task<StrOrNum[][]>> resolve)
    {
        StrOrNum[][] parValues = await resolve(builder.FullName).ConfigureAwait(false);

        if (parValues.Length == 0 && builder.Settings.PartByListFieldNames.Count > 0)
        {
            // A list-partitioned table needs one value per list level; with none resolved there is
            // no leaf to create. This used to emit the root and the list levels only, producing a
            // tree whose deepest level had no range leaf - every later INSERT then failed with
            // "no partition of relation ... found for row". Nothing to do is the honest answer.
            yield break;
        }

        bool rootEmitted = false;

        string Next(DateTimeOffset date, StrOrNum[] parts)
        {
            if (rootEmitted) return builder.CreatePartSql(date, parts);

            rootEmitted = true;
            return builder.CreateSql(date, parts);
        }

        if (parValues.Length > 0)
        {
            foreach (StrOrNum[] parts in parValues)
            {
                foreach (DateTimeOffset date in dates)
                {
                    yield return new MigrateBatch(builder.FullName, Next(date, parts), null);
                }
            }
        }
        else
        {
            foreach (DateTimeOffset date in dates)
            {
                yield return new MigrateBatch(builder.FullName, Next(date, []), null);
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
