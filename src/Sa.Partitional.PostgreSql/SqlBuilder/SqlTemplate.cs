using Sa.Classes;
using Sa.Extensions;
using Sa.Partitional.PostgreSql.Classes;
using System.Globalization;
using System.Text;

namespace Sa.Partitional.PostgreSql.SqlBuilder;

internal static class SqlTemplate
{
    private const char NumOrStrSplitter = ',';

    /// <summary>
    /// PostgreSQL truncates identifiers to 63 <b>bytes</b> — not characters. For a non-ASCII
    /// partition value the UTF-8 byte count exceeds <c>string.Length</c>, so the limit has to be
    /// checked against the encoded form.
    /// </summary>
    private const int MaxIdentifierBytes = 63;

    /// <summary>
    /// Escapes <paramref name="value"/> for use as a single-quoted SQL string literal.
    /// Embedded single quotes are doubled (<c>'</c> to <c>''</c>); that is PostgreSQL's only
    /// escape mechanism inside a literal, there is no backslash escaping.
    /// </summary>
    private static string QuoteLiteral(string? value)
        => value is null ? "NULL" : $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>
    /// Escapes <paramref name="name"/> and wraps it in double quotes so it can be used as a SQL
    /// identifier. Embedded double quotes are doubled (<c>"</c> to <c>""</c>).
    /// </summary>
    /// <remarks>
    /// Partition values reach the generated DDL through the partition <i>table name</i>, so this
    /// sits on the untrusted-data path: quoting without escaping would still let a value holding
    /// <c>"</c> terminate the identifier and append SQL.
    /// </remarks>
    private static string QuoteIdentifier(string? name)
        => $"\"{(name ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <summary>
    /// Renders a partition key value as a SQL literal: strings are quoted and escaped, numbers
    /// are emitted with the invariant culture.
    /// </summary>
    private static string QuoteValue(StrOrNum value)
        => value.Match(s => QuoteLiteral(s), n => n.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// public.customer
    /// </summary>
    public static string CreateRootSql(this ITableSettings settings)
    {
        bool partByRangeExists = HasColumn(settings, settings.PartByRangeFieldName);
        string pkColumns = GetPrimaryKeyColumns(settings);

        var rangeFieldDefinition = partByRangeExists
            ? string.Empty
            : $"{QuoteIdentifier(settings.PartByRangeFieldName)} bigint NOT NULL,";

        return $"""
CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(settings.DatabaseSchemaName)};

CREATE TABLE IF NOT EXISTS {settings.GetQualifiedTableName()} (
  {settings.Fields.JoinByString($",{Environment.NewLine}  ")},
  {rangeFieldDefinition}
  CONSTRAINT {QuoteIdentifier(settings.Pk())} PRIMARY KEY ({pkColumns},{QuoteIdentifier(settings.PartByRangeFieldName)})
) {settings.GetPartitionalSql(0)};

-- post sql
{settings.PostRootSql?.Invoke()}

""";
    }

    private static string GetPrimaryKeyColumns(ITableSettings settings)
    {
        return settings.PartByListFieldNames.Contains(settings.IdFieldName)
            ? settings.PartByListFieldNames.JoinByString(QuoteIdentifier, ",")
            : new string[] { settings.IdFieldName }
                .Concat(settings.PartByListFieldNames)
                .JoinByString(QuoteIdentifier, ",");
    }

    /// <summary>
    /// Reports whether <paramref name="columnName"/> is declared among <paramref name="settings"/>'s
    /// raw field definitions, compared against the extracted column name.
    /// </summary>
    private static bool HasColumn(ITableSettings settings, string columnName)
        => SqlFieldName.HasColumn(settings.Fields, columnName);

    /// <summary>
    /// DDL для создания вложенной партиции (nested list partition).
    /// Первая строка — аннотирующий комментарий «NESTED PARTITION»;
    /// ниже — стандартный CREATE TABLE ... PARTITION OF ... FOR VALUES IN (...).
    /// </summary>
    public static string CreateNestedSql(this ITableSettings settings, StrOrNum[] values)
        => CreateNestedSql(settings, values.AsSpan());

    /// <summary>
    /// Span view of <see cref="CreateNestedSql(ITableSettings, StrOrNum[])"/>: takes a <i>view</i>
    /// of the partition values (the caller's array is never copied), so a caller building several
    /// nested levels can pass growing prefix views of one array instead of allocating a fresh
    /// <c>StrOrNum[]</c> slice per level. The parent is the same view minus its last element, and the
    /// value literal is the last element — both read in place, no copy.
    /// </summary>
    internal static string CreateNestedSql(this ITableSettings settings, ReadOnlySpan<StrOrNum> values) =>
$"""


-- NESTED PARTITION

CREATE TABLE IF NOT EXISTS {settings.GetQualifiedTableName(values)}
PARTITION OF {settings.GetQualifiedTableName(values[..^1])}
FOR VALUES IN ({QuoteValue(values[^1])})
{settings.GetPartitionalSql(values.Length)}
;
""";


    // Вспомогательный метод
    private static string GetFillFactorClause(this ITableSettings settings)
    {
        // null means "not configured": the clause is omitted and PostgreSQL applies its own default
        // (100). A 0 would have meant the same thing, but as a value it read like a real setting.
        return settings.FillFactor is int fillFactor && fillFactor > 0
            ? $" WITH (fillfactor = {fillFactor})"
            : "";
    }

    /// <summary>
    /// public."customer_FR_Bordeaux_y2025m01d08"
    /// </summary>
    public static string CreatePartByRangeSql(this ITableSettings settings, DateTimeOffset date, StrOrNum[] values)
    {
        // Hand the caller's array straight through as a span view (no copy): the leaf name is
        // built from the span plus the range suffix, and the parent name from the same span.
        string timeRangeTablename = settings.GetQualifiedTableName(date, values.AsSpan());
        LimSection<DateTimeOffset> range = settings.PartBy.GetRange(date);
        string cacheTablename = settings.GetCacheByRangeTableName();
        var partValues = StrOrNumsToFmtString(values);

        return
$"""


-- ({settings.PartByRangeFieldName})  part by: {settings.PartBy}

CREATE TABLE IF NOT EXISTS {timeRangeTablename}
PARTITION OF {settings.GetQualifiedTableName(values.AsSpan())}
FOR VALUES FROM ({range.Start.ToUnixTimeSeconds()}) TO ({range.End.ToUnixTimeSeconds()})
{settings.GetFillFactorClause()}
;

-- cache

CREATE TABLE IF NOT EXISTS {cacheTablename} (
  id TEXT PRIMARY KEY,
  root TEXT NOT NULL,
  part_values TEXT NOT NULL,
  part_by TEXT NOT NULL,
  from_date bigint NOT NULL,
  to_date bigint NOT NULL,
  archived boolean NOT NULL DEFAULT FALSE
)
;

ALTER TABLE IF EXISTS {cacheTablename} ADD COLUMN IF NOT EXISTS archived boolean NOT NULL DEFAULT FALSE;

INSERT INTO {cacheTablename} (id,root,part_values,part_by,from_date,to_date)
VALUES ({QuoteLiteral(timeRangeTablename)},{QuoteLiteral(settings.FullName)},{QuoteLiteral(partValues)},{QuoteLiteral(settings.PartBy.Name)},{range.Start.ToUnixTimeSeconds()},{range.End.ToUnixTimeSeconds()})
ON CONFLICT (id) DO NOTHING
;

""";
    }

    public static string SelectPartsFromDateSql(this ITableSettings settings)
    {
        return
$"""
SELECT id,root,part_values,part_by,from_date
FROM {settings.GetCacheByRangeTableName()}
WHERE root = {QuoteLiteral(settings.FullName)} AND archived = FALSE AND from_date >= @from_date
ORDER BY from_date DESC
;
""";
    }


    public static string SelectPartsToDateSql(this ITableSettings settings)
    {
        return
$"""
SELECT id,root,part_values,part_by,from_date
FROM {settings.GetCacheByRangeTableName()}
WHERE root = {QuoteLiteral(settings.FullName)} AND archived = FALSE AND to_date <= @to_date
ORDER BY from_date ASC
;
""";
    }


    public static string SelectPartsQualifiedTablesSql(this ITableSettings settings, StrOrNum[] values)
        => SelectPartsQualifiedTablesSql(settings.GetQualifiedTableName(values.AsSpan()));

    internal static string SelectPartsQualifiedTablesSql(this ITableSettings settings, ReadOnlySpan<StrOrNum> values)
        => SelectPartsQualifiedTablesSql(settings.GetQualifiedTableName(values));

    public static string SelectPartsQualifiedTablesSql(string qualifiedTableName)
        =>
$"""
WITH pt AS (
  SELECT inhrelid::regclass AS pt
  FROM pg_inherits
  WHERE inhparent = {QuoteLiteral(qualifiedTableName)}::regclass
)
SELECT pt::text from pt
;
""";


    public static string DropPartSql(this ITableSettings settings, string qualifiedTableName)
    {
        return $"""
DROP TABLE IF EXISTS {qualifiedTableName};
DELETE FROM {settings.GetCacheByRangeTableName()} WHERE id={QuoteLiteral(qualifiedTableName)};
""";
    }

    /// <summary>
    /// Ensures the archive schema and the <c>archived</c> column of the cache table exist. Idempotent,
    /// so it runs on every archive pass: a cache table created before archiving was introduced is
    /// upgraded in place, and a fresh one already carries the column.
    /// </summary>
    public static string EnsureArchiveSql(this ITableSettings settings, string archiveSchemaName)
    {
        return $"""
CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(archiveSchemaName)};
ALTER TABLE IF EXISTS {settings.GetCacheByRangeTableName()} ADD COLUMN IF NOT EXISTS archived boolean NOT NULL DEFAULT FALSE;
""";
    }

    /// <summary>
    /// Reports the state of one partition from the <b>source</b> point of view: whether it still sits
    /// in its original schema, whether it already reached the archive schema, and whether it is still
    /// attached to its parent. A resumed run needs all three to finish what an interrupted run left
    /// half-done without repeating a step that already succeeded.
    /// </summary>
    /// <param name="sourceQualifiedName">The qualified partition name as recorded in the cache table.</param>
    /// <param name="archiveQualifiedName">The same bare name qualified with the archive schema.</param>
    /// <param name="parentQualifiedName">The qualified immediate parent (the list level, or the root).</param>
    public static string PartStateSql(
        string sourceQualifiedName,
        string archiveQualifiedName,
        string parentQualifiedName)
    {
        return $"""
SELECT
  (to_regclass({QuoteLiteral(sourceQualifiedName)}) IS NOT NULL),
  (to_regclass({QuoteLiteral(archiveQualifiedName)}) IS NOT NULL),
  EXISTS (
    SELECT 1 FROM pg_inherits
    WHERE inhrelid = to_regclass({QuoteLiteral(sourceQualifiedName)})
      AND inhparent = to_regclass({QuoteLiteral(parentQualifiedName)})
  );
""";
    }

    /// <summary>
    /// Detaches a partition from its parent using <c>CONCURRENTLY</c> (reduced locking). Must be
    /// executed as a <b>single</b> statement: PostgreSQL forbids
    /// <c>DETACH PARTITION CONCURRENTLY</c> inside a transaction block.
    /// </summary>
    public static string DetachPartSql(this ITableSettings settings, StrOrNum[] partValues, string qualifiedPartName)
        => $"ALTER TABLE {settings.GetQualifiedTableName(partValues.AsSpan())} DETACH PARTITION CONCURRENTLY {qualifiedPartName};\n";

    /// <summary>
    /// Completes a <c>DETACH PARTITION CONCURRENTLY</c> that an earlier run left pending (the
    /// partition is marked as undergoing detach, and a new detach of the same parent is refused until
    /// it is finalised). Single statement, like the detach itself.
    /// </summary>
    public static string FinalizeDetachPartSql(this ITableSettings settings, StrOrNum[] partValues, string qualifiedPartName)
        => $"ALTER TABLE {settings.GetQualifiedTableName(partValues.AsSpan())} DETACH PARTITION {qualifiedPartName} FINALIZE;\n";

    /// <summary>
    /// Moves an already detached partition into the archive schema (its indexes follow the table) and
    /// marks the metadata row as archived. Idempotent: the move is skipped by the caller when the
    /// partition is already in the archive schema, and the <c>UPDATE</c> simply matches nothing twice.
    /// </summary>
    public static string ArchivePartSql(this ITableSettings settings, string qualifiedPartName, string archiveSchemaName)
    {
        return $"""
ALTER TABLE {qualifiedPartName} SET SCHEMA {QuoteIdentifier(archiveSchemaName)};
UPDATE {settings.GetCacheByRangeTableName()} SET archived = TRUE WHERE id={QuoteLiteral(qualifiedPartName)};
""";
    }

    /// <summary>
    /// Marks the metadata row as archived without touching the table — the partition already sits in
    /// the archive schema (a run that moved it but did not get to the bookkeeping).
    /// </summary>
    public static string MarkArchivedSql(this ITableSettings settings, string qualifiedPartName)
        => $"UPDATE {settings.GetCacheByRangeTableName()} SET archived = TRUE WHERE id={QuoteLiteral(qualifiedPartName)};\n";

    /// <summary>
    /// Quotes and qualifies a <c>schema.name</c> pair as two SQL identifiers.
    /// </summary>
    internal static string QualifiedName(string schemaName, string tableName)
        => $"{QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)}";

    public static string GetQualifiedTableName(this ITableSettings settings, DateTimeOffset? date, StrOrNum[] values)
        => settings.GetQualifiedTableName(date, values.AsSpan());

    /// <summary>
    /// Span view of <see cref="GetQualifiedTableName(ITableSettings, DateTimeOffset?, StrOrNum[])"/>.
    /// The range suffix (e.g. <c>y2025m01d08</c>) is appended <i>inside</i> the name join instead of
    /// materialising a <c>[.. values, suffix]</c> array, so the caller's array is never copied.
    /// </summary>
    internal static string GetQualifiedTableName(this ITableSettings settings, DateTimeOffset? date, ReadOnlySpan<StrOrNum> values)
        => date is null
        ? settings.GetQualifiedTableNameCore(settings.GetPartTableName(values))
        : settings.GetQualifiedTableNameCore(settings.GetPartTableName(values, settings.PartBy.Fmt(date.Value)));

    private static string GetCacheByRangeTableName(this ITableSettings settings)
        => settings.GetQualifiedTableName(settings.PartTablePostfix);


    /// <summary>
    /// Public entry point: builds the partition name from an array of values.
    /// </summary>
    static string GetPartTableName(this ITableSettings settings, params StrOrNum[] values)
        => settings.GetPartTableName(values.AsSpan());

    /// <summary>
    /// Builds the (unqualified) partition table name from a <i>view</i> of the partition values,
    /// so callers can pass a slice without copying the underlying array. When <paramref name="suffix"/>
    /// is supplied (the range token for a leaf) it is appended as the final join element rather than
    /// the caller materialising a <c>[.. values, suffix]</c> array.
    /// </summary>
    internal static string GetPartTableName(this ITableSettings settings, ReadOnlySpan<StrOrNum> values, string? suffix = null)
        => BuildPartTableName(settings, values, suffix);

    /// <summary>
    /// The single implementation of partition-name composition: <c>table{sep}v1{sep}v2…{sep}vN</c>,
    /// plus an optional trailing <c>{sep}{suffix}</c> for a range leaf. The values are joined from the
    /// span view (no copy of the caller's array); the per-value strings are collected into one small
    /// buffer that <see cref="string.Join(string?, string[]?)"/> consumes — the same ToString work
    /// <c>string.Join</c> always did, just without the extra <c>StrOrNum[]</c> copies the sliced
    /// overloads used to make.
    /// </summary>
    static string BuildPartTableName(this ITableSettings settings, ReadOnlySpan<StrOrNum> values, string? suffix = null)
    {
        int count = values.Length + (suffix is null ? 0 : 1);

        if (count == 0)
            return settings.DatabaseTableName;

        // One buffer for the per-value strings (and the optional range suffix); string.Join consumes
        // it. The suffix is appended as the final join element instead of the caller materialising a
        // [.. values, suffix] array.
        string[] parts = new string[count];
        for (int i = 0; i < values.Length; i++)
            parts[i] = values[i].ToString();

        if (suffix is not null)
            parts[values.Length] = suffix;

        return string.Concat(
            settings.DatabaseTableName,
            settings.SqlPartSeparator,
            string.Join(settings.SqlPartSeparator, parts));
    }

    /// <summary>
    /// Public entry point: qualifies (schema-qualifies + quotes) a partition name built from an
    /// array of values, enforcing the 63-byte UTF-8 identifier limit.
    /// </summary>
    static string GetQualifiedTableName(this ITableSettings settings, params StrOrNum[] values)
        => settings.GetQualifiedTableNameCore(settings.GetPartTableName(values));

    /// <summary>
    /// Span view of <see cref="GetQualifiedTableName(ITableSettings, StrOrNum[])"/>.
    /// </summary>
    internal static string GetQualifiedTableName(this ITableSettings settings, ReadOnlySpan<StrOrNum> values)
        => settings.GetQualifiedTableNameCore(settings.GetPartTableName(values));

    /// <summary>
    /// The shared validation + qualification step for both the array and the span
    /// <see cref="GetQualifiedTableName"/> entry points. Keeping it in one place means the 63-byte
    /// UTF-8 check and its exception cannot drift between the two.
    /// </summary>
    static string GetQualifiedTableNameCore(this ITableSettings settings, string tableName)
    {
        // 63 BYTES, not 63 chars: PostgreSQL truncates on the UTF-8 encoding, and a non-ASCII
        // partition value has more UTF-8 bytes than string.Length. A name PostgreSQL would truncate
        // cannot reach the cache table, because the DDL and the stored identifier would no longer match.
        if (Encoding.UTF8.GetByteCount(tableName) > MaxIdentifierBytes)
        {
            throw new InvalidOperationException(
                $"Table name '{tableName}' exceeds PostgreSQL's {MaxIdentifierBytes}-byte limit for identifiers. ");
        }

        return $"{QuoteIdentifier(settings.DatabaseSchemaName)}.{QuoteIdentifier(tableName)}";
    }

    static string GetPartitionalSql(this ITableSettings settings, int partIndex)
        => partIndex >= 0 && partIndex < settings.PartByListFieldNames.Length
            ? $"PARTITION BY LIST ({QuoteIdentifier(settings.PartByListFieldNames[partIndex])})"
            : $"PARTITION BY RANGE ({QuoteIdentifier(settings.PartByRangeFieldName)})"
            ;

    static string Pk(this ITableSettings settings)
        => settings.ConstraintPkSql?.Invoke() ?? $"pk_{settings.DatabaseTableName}";


    /// <summary>
    /// Splits the <c>part_values</c> column back into its values. The split honours the escapes
    /// written by <see cref="StrOrNum.ToFmtString"/>: a comma inside a value is stored as
    /// <c>\,</c> and must not split here, otherwise a partition value such as <c>"a,b"</c> would come
    /// back as two values and no longer match the request that created it.
    /// </summary>
    internal static StrOrNum[] ParseStrOrNums(string fmtInput)
    {
        ReadOnlySpan<char> span = fmtInput.AsSpan();

        if (span.IsEmpty) return [];

        List<StrOrNum> result = new(span.Count(NumOrStrSplitter) + 1);

        int start = 0;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] != NumOrStrSplitter || StrOrNum.IsEscapedAt(span, i)) continue;

            // RemoveEmptyEntries: a leading, trailing or doubled separator yields nothing.
            if (i > start) result.Add(StrOrNum.FromFmtStr(span[start..i].ToString()));
            start = i + 1;
        }

        if (start < span.Length) result.Add(StrOrNum.FromFmtStr(span[start..].ToString()));

        return [.. result];
    }

    private static string StrOrNumsToFmtString(StrOrNum[] input)
        => string.Join(NumOrStrSplitter, input.Select(c => c.ToFmtString()));

}
