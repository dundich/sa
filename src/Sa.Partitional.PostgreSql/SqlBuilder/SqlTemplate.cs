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
    /// Embedded single quotes are doubled (<c>'</c> to <c>''</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plain <c>'…'</c> form is emitted by default: with <c>standard_conforming_strings = on</c>
    /// (the server default since PostgreSQL 9.1, and what Npgsql sets on its connections) a backslash
    /// has no meaning inside it, so doubling the quote is the whole escape.
    /// </para>
    /// <para>
    /// A value that <i>does</i> contain a backslash is rendered as an explicit escape string
    /// (<c>E'…'</c>) with every backslash doubled. <c>E</c> forces backslash escaping whatever the
    /// server setting is, so a value ending in <c>\</c> can never swallow the closing quote and let
    /// the remainder of the value be parsed as SQL.
    /// </para>
    /// </remarks>
    private static string QuoteLiteral(string? value)
    {
        if (value is null) return "NULL";

        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
        }

        return $"E'{value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "''", StringComparison.Ordinal)}'";
    }

    /// <summary>
    /// Escapes <paramref name="name"/> and wraps it in double quotes so it can be used as a SQL
    /// identifier. Embedded double quotes are doubled (<c>"</c> to <c>""</c>).
    /// </summary>
    /// <remarks>
    /// Partition values reach the generated DDL through the partition <i>table name</i>, so this
    /// sits on the untrusted-data path: quoting without escaping would still let a value holding
    /// <c>"</c> terminate the identifier and append SQL.
    /// </remarks>
    internal static string QuoteIdentifier(string? name)
        => $"\"{(name ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";    /// <summary>
    /// Renders a partition key value as a SQL literal: strings are quoted and escaped, numbers
    /// are emitted with the invariant culture.
    /// </summary>
    private static string QuoteValue(StrOrNum value)
        => value.Match(s => QuoteLiteral(s), n => n.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Quotes a qualified table name that came <i>back</i> from the database (the partition cache's
    /// <c>id</c> column, <c>regclass</c> output) so it can be embedded in DDL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DROP TABLE</c>, <c>ALTER TABLE … DETACH PARTITION</c> and <c>ALTER TABLE … SET SCHEMA</c>
    /// do not take parameters, and the partition name has to be spliced into the statement. These
    /// names are written by <see cref="GetQualifiedTableNameCore"/> and are already quoted there, but
    /// they are read back from a table and are therefore the one place where stored data reaches
    /// SQL. Parsing the name into its component identifiers and quoting each one again means a row
    /// that does not have the expected <c>schema.table</c> shape can only ever become a quoted
    /// identifier — never an extra statement.
    /// </para>
    /// <para>
    /// Whatever the caller does with the returned text, the <i>original</i> string is the key of the
    /// cache row and has to be used as-is (with <see cref="QuoteLiteral"/>) in the
    /// <c>DELETE</c>/<c>UPDATE</c> that follows.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The name is not a qualified identifier.</exception>
    internal static string RequoteStoredQualifiedName(string qualifiedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedName);

        if (!TrySplitQualifiedName(qualifiedName, out string[] parts))
        {
            throw new InvalidOperationException(
                $"'{qualifiedName}' is not a table name of the form schema.table; refusing to put it into DDL. " +
                "The partition cache row is corrupt.");
        }

        return string.Join('.', parts.Select(QuoteIdentifier));
    }

    /// <summary>
    /// Splits a (possibly quoted) qualified name into its parts, unescaping doubled quotes inside a
    /// quoted identifier: <c>"sc""h"."my.table"</c> becomes <c>sc"h</c> and <c>my.table</c>.
    /// </summary>
    /// <param name="qualifiedName">The name to split.</param>
    /// <param name="parts">
    /// One or two unquoted identifiers, or an empty array when the name is malformed.
    /// </param>
    /// <returns><see langword="true"/> when the name is a well-formed 1- or 2-part identifier.</returns>
    /// <remarks>
    /// Unquoted parts cannot contain a dot — the dot <i>is</i> the separator — so splitting on it
    /// outside quotes is exactly right; inside quotes it is ordinary text.
    /// </remarks>
    internal static bool TrySplitQualifiedName(string qualifiedName, out string[] parts)
    {
        parts = [];

        if (string.IsNullOrEmpty(qualifiedName)) return false;

        List<string> result = new(2);
        var current = new StringBuilder(qualifiedName.Length);

        bool inQuotes = false;

        for (int i = 0; i < qualifiedName.Length; i++)
        {
            char c = qualifiedName[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // A doubled quote inside a quoted identifier is the escaped quote itself.
                    if (i + 1 < qualifiedName.Length && qualifiedName[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                        continue;
                    }

                    inQuotes = false;
                    continue;
                }

                current.Append(c);
                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
                continue;
            }

            if (c == '.')
            {
                if (current.Length == 0) return false;

                result.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        // An unterminated quote or an empty last part ("public.") is not a name we can quote back.
        if (inQuotes || current.Length == 0) return false;

        result.Add(current.ToString());

        if (result.Count > 2) return false;

        parts = [.. result];
        return true;
    }

    /// <summary>
    /// Flattens free text so it can be embedded in a single-line <c>--</c> SQL comment.
    /// </summary>
    /// <remarks>
    /// A newline in the injected text would end the comment and turn the rest of it into live SQL.
    /// The values that reach a comment (field and strategy names) are developer-provided, so they are
    /// flattened rather than quoted.
    /// </remarks>
    internal static string SingleLineCommentText(string? value)
        => value is null ? string.Empty : value.Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>
    /// Name token of the optional <c>DEFAULT</c> partition. It is a reserved name, not a partition
    /// value: the table is emitted with <c>PARTITION OF ... DEFAULT</c> and captures every row that
    /// does not match any explicitly created sibling partition. Kept short (<c>def$</c>, marked with
    /// <c>$</c> like the <c>part$</c> cache table) because it is appended to the full list prefix and
    /// eats into the 63-byte identifier budget. A list value literally named <c>def$</c> would
    /// therefore generate the same table name.
    /// </summary>
    internal const string DefaultPartitionName = "def$";

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

        // A partitioned table may hold at most one DEFAULT partition per parent, so the optional
        // catch-all sits directly under the root and is emitted from the root DDL (idempotent).
        // Deeper levels get their own default when their partition is created (see SqlPartListBuilder).
        string defaultPartDefinition = settings.DefaultPartition
            ? settings.CreateDefaultPartSql(ReadOnlySpan<StrOrNum>.Empty)
            : string.Empty;

        return $"""
CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(settings.DatabaseSchemaName)};

CREATE TABLE IF NOT EXISTS {settings.GetQualifiedTableName()} (
  {settings.Fields.JoinByString($",{Environment.NewLine}  ")},
  {rangeFieldDefinition}
  CONSTRAINT {QuoteIdentifier(settings.Pk())} PRIMARY KEY ({pkColumns},{QuoteIdentifier(settings.PartByRangeFieldName)})
) {settings.GetPartitionalSql(0)};
{defaultPartDefinition}
-- post sql
{settings.PostRootSql?.Invoke()}

""";
    }

    /// <summary>
    /// public.customer__def$  /  public.customer__FR__def$
    /// </summary>
    /// <remarks>
    /// DDL of the optional <c>DEFAULT</c> partition of the parent identified by <paramref name="values"/>:
    /// the root when the span is empty, otherwise the list prefix. The catch-all is a plain leaf — it is
    /// <i>not</i> further partitioned, so a row reaches it because its list value did not match any
    /// explicit sibling partition, or, at the deepest (range) level, because its date fell outside every
    /// created range. Only meaningful when <see cref="ITableSettings.DefaultPartition"/> is set; the
    /// caller decides whether to emit it.
    /// </remarks>
    public static string CreateDefaultPartSql(this ITableSettings settings, params StrOrNum[] values)
        => settings.CreateDefaultPartSql(values.AsSpan());

    /// <summary>
    /// Span view of <see cref="CreateDefaultPartSql(ITableSettings, StrOrNum[])"/>.
    /// </summary>
    public static string CreateDefaultPartSql(this ITableSettings settings, ReadOnlySpan<StrOrNum> values)
    {
        string qualifiedDefaultName = settings.GetQualifiedDefaultTableName(values);

        return $"""


-- DEFAULT PARTITION

CREATE TABLE IF NOT EXISTS {qualifiedDefaultName}
PARTITION OF {settings.GetQualifiedTableName(values)}
DEFAULT
;
""";
    }

    /// <summary>
    /// Qualified name of the DEFAULT partition that catches rows of the parent identified by
    /// <paramref name="values"/> (the root when the span is empty).
    /// </summary>
    internal static string GetQualifiedDefaultTableName(this ITableSettings settings, ReadOnlySpan<StrOrNum> values)
        => settings.GetQualifiedTableNameCore(settings.GetPartTableName(values, DefaultPartitionName));

    /// <summary>
    /// <c>field = 'value'</c> — the rows a list partition of <paramref name="fieldIndex"/> with
    /// <paramref name="value"/> owns, regardless of the deeper keys.
    /// </summary>
    internal static string ListPartPredicate(this ITableSettings settings, int fieldIndex, StrOrNum value)
        => $"{QuoteIdentifier(settings.PartByListFieldNames[fieldIndex])} = {QuoteValue(value)}";

    /// <summary>
    /// <c>range_field &gt;= start AND range_field &lt; end</c> — the rows the range leaf of
    /// <paramref name="date"/> owns. The range column is the bigint unix-seconds key the library
    /// partitions on, so the bounds are integers.
    /// </summary>
    internal static string RangePartPredicate(this ITableSettings settings, DateTimeOffset date)
    {
        LimSection<DateTimeOffset> range = settings.PartBy.GetRange(date);
        string column = QuoteIdentifier(settings.PartByRangeFieldName);

        return $"{column} >= {settings.RangeBoundSql(range.Start)} AND {column} < {settings.RangeBoundSql(range.End)}";
    }

    /// <summary>
    /// Renders one range bound as a SQL literal matching the type of the range key: an integer for
    /// the <c>bigint</c> Unix-seconds mode, a quoted UTC timestamp for a timestamp column, or a date.
    /// </summary>
    internal static string RangeBoundSql(this ITableSettings settings, DateTimeOffset value)
    {
        DateTime utc = value.UtcDateTime;

        return settings.RangeKeyType switch
        {
            RangeKeyType.UnixSeconds
                => value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),

            RangeKeyType.TimestampTz
                => QuoteLiteral(utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "+00"),

            RangeKeyType.Timestamp
                => QuoteLiteral(utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),

            RangeKeyType.Date
                => QuoteLiteral(utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),

            _ => throw new InvalidOperationException(
                $"Table '{settings.FullName}' has an unsupported range key type '{settings.RangeKeyType}'."),
        };
    }

    /// <summary>
    /// Moves the rows of the parent's DEFAULT partition that belong to a soon-to-be-created sibling
    /// (selected by <paramref name="predicate"/>) into a transaction-scoped temporary table.
    /// </summary>
    /// <remarks>
    /// PostgreSQL refuses to create a partition while the existing DEFAULT partition holds rows that
    /// would belong to it (<c>updated partition constraint for default partition would be violated by
    /// some row</c>). Pulling them out first — and putting them back with
    /// <see cref="MoveRowsBackSql"/> after the partition exists — keeps the migration a single,
    /// self-healing transaction. <c>ON COMMIT DROP</c> ties the staging table to the implicit
    /// transaction of the batch, so an interrupted run leaves neither the table nor half-moved rows.
    /// </remarks>
    internal static string MoveRowsOutOfDefaultSql(string defaultQualifiedName, string predicate, string tempTableName)
    {
        string temp = QuoteIdentifier(tempTableName);
        // Qualify the defensive drop with pg_temp so a real table that happens to share the staging
        // name is never touched; the create/reference stay unqualified and resolve to the temp table.
        string tempDrop = QuoteIdentifier("pg_temp") + "." + temp;

        return $"""


-- DEFAULT reconciliation: pull rows that belong to the new partition out of the default
DROP TABLE IF EXISTS {tempDrop};

CREATE TEMP TABLE {temp} (LIKE {defaultQualifiedName}) ON COMMIT DROP;

INSERT INTO {temp}
SELECT * FROM {defaultQualifiedName} WHERE {predicate};

DELETE FROM {defaultQualifiedName} WHERE {predicate};
""";
    }

    /// <summary>
    /// Puts the rows staged by <see cref="MoveRowsOutOfDefaultSql"/> back, now into the partition that
    /// has just been created. Must run in the same batch so it sees the staging table.
    /// </summary>
    /// <remarks>
    /// <c>OVERRIDING SYSTEM VALUE</c> keeps an identity key column insertable: <c>SELECT *</c> carries
    /// the staged <c>id</c> explicitly, and PostgreSQL refuses an explicit value into a
    /// <c>GENERATED ALWAYS AS IDENTITY</c> column without it (<c>22014</c>). For a <c>BY DEFAULT</c>
    /// identity or a plain column the clause is a no-op, so it is emitted unconditionally.
    /// </remarks>
    internal static string MoveRowsBackSql(string newPartitionQualifiedName, string tempTableName)
        => $"""

INSERT INTO {newPartitionQualifiedName} OVERRIDING SYSTEM VALUE
SELECT * FROM {QuoteIdentifier(tempTableName)};
""";

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
        var partValues = StrOrNumsToFmtString(values);

        return
$"""


-- ({SqlTemplate.SingleLineCommentText(settings.PartByRangeFieldName)})  part by: {SqlTemplate.SingleLineCommentText(settings.PartBy.Name)}

CREATE TABLE IF NOT EXISTS {timeRangeTablename}
PARTITION OF {settings.GetQualifiedTableName(values.AsSpan())}
FOR VALUES FROM ({settings.RangeBoundSql(range.Start)}) TO ({settings.RangeBoundSql(range.End)})
{settings.GetFillFactorClause()}
;

-- cache

{settings.CreateCacheSql()}
INSERT INTO {settings.GetCacheByRangeTableName()} (id,root,part_values,part_by,from_date,to_date)
VALUES ({QuoteLiteral(timeRangeTablename)},{QuoteLiteral(settings.FullName)},{QuoteLiteral(partValues)},{QuoteLiteral(settings.PartBy.Name)},{range.Start.ToUnixTimeSeconds()},{range.End.ToUnixTimeSeconds()})
ON CONFLICT (id) DO NOTHING
;

""";
    }

    /// <summary>
    /// Creates the partition cache table and upgrades an existing one (<c>archived</c> column).
    /// </summary>
    /// <remarks>
    /// The range-leaf DDL creates the cache table inline. The adoption path needs it separately: the
    /// cache table is also the marker that tells a later start "this table is already managed", and
    /// an adopted table may have no rows at all, so no leaf DDL would ever run.
    /// </remarks>
    public static string CreateCacheSql(this ITableSettings settings)
    {
        string cacheTablename = settings.GetCacheByRangeTableName();

        return $"""
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


    public static string DropPartSql(this ITableSettings settings, string qualifiedTableName)
    {
        return $"""
DROP TABLE IF EXISTS {RequoteStoredQualifiedName(qualifiedTableName)};
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
        => $"ALTER TABLE {settings.GetQualifiedTableName(partValues.AsSpan())} DETACH PARTITION {RequoteStoredQualifiedName(qualifiedPartName)} CONCURRENTLY;\n";

    /// <summary>
    /// Completes a <c>DETACH PARTITION CONCURRENTLY</c> that an earlier run left pending (the
    /// partition is marked as undergoing detach, and a new detach of the same parent is refused until
    /// it is finalised). Single statement, like the detach itself.
    /// </summary>
    public static string FinalizeDetachPartSql(this ITableSettings settings, StrOrNum[] partValues, string qualifiedPartName)
        => $"ALTER TABLE {settings.GetQualifiedTableName(partValues.AsSpan())} DETACH PARTITION {RequoteStoredQualifiedName(qualifiedPartName)} FINALIZE;\n";

    /// <summary>
    /// Moves an already detached partition into the archive schema (its indexes follow the table) and
    /// marks the metadata row as archived. Idempotent: the move is skipped by the caller when the
    /// partition is already in the archive schema, and the <c>UPDATE</c> simply matches nothing twice.
    /// </summary>
    public static string ArchivePartSql(this ITableSettings settings, string qualifiedPartName, string archiveSchemaName)
    {
        return $"""
ALTER TABLE {RequoteStoredQualifiedName(qualifiedPartName)} SET SCHEMA {QuoteIdentifier(archiveSchemaName)};
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
    /// <remarks>
    /// A value that itself contains <see cref="ITableSettings.SqlPartSeparator"/> is rejected: it
    /// would make two different value sets produce the same table name. Note also that the name is
    /// built from <see cref="StrOrNum.ToString"/>, so the string <c>"5"</c> and the number <c>5</c>
    /// render the same name while producing different partition bounds (<c>'5'</c> vs <c>5</c>) —
    /// one table must therefore use one kind of value per position.
    /// </remarks>
    static string BuildPartTableName(this ITableSettings settings, ReadOnlySpan<StrOrNum> values, string? suffix = null)
    {
        int count = values.Length + (suffix is null ? 0 : 1);

        if (count == 0)
            return settings.DatabaseTableName;

        string separator = settings.SqlPartSeparator;

        // One buffer for the per-value strings (and the optional range suffix); string.Join consumes
        // it. The suffix is appended as the final join element instead of the caller materialising a
        // [.. values, suffix] array.
        string[] parts = new string[count];
        for (int i = 0; i < values.Length; i++)
        {
            string part = values[i].ToString();

            // The name is root{sep}v1{sep}v2…, so a separator *inside* a value makes the name
            // ambiguous: ("a__b", "c") and ("a", "b__c") produce one and the same table while asking
            // for two different partition bounds. The second CREATE TABLE IF NOT EXISTS would silently
            // no-op and its rows would land in the first partition. Failing here is the only outcome
            // that cannot lose data; the caller either renames the value or picks another separator
            // with WithPartSeparator.
            if (part.Contains(separator, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Partition value '{part}' contains the partition separator '{separator}' of table "
                    + $"'{settings.FullName}'. That makes the generated partition name ambiguous "
                    + "(a__b+c and a+b__c would be the same table); rename the value or configure "
                    + "another separator with WithPartSeparator.");
            }

            parts[i] = part;
        }

        if (suffix is not null)
            parts[values.Length] = suffix;

        return string.Concat(
            settings.DatabaseTableName,
            separator,
            string.Join(separator, parts));
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
        int bytes = Encoding.UTF8.GetByteCount(tableName);

        if (bytes > MaxIdentifierBytes)
        {
            // Spell the arithmetic out. The caller's next question is always "how short does the
            // value have to be?", and the answer is the fixed part of the name - the root name, the
            // separators and the range suffix - which is known here. A bare "exceeds 63 bytes" left
            // that reconstruction to the reader.
            string suffix = settings.PartBy.Fmt(DateTimeOffset.UnixEpoch);

            int fixedBytes = Encoding.UTF8.GetByteCount(settings.DatabaseTableName)
                + Encoding.UTF8.GetByteCount(suffix);

            throw new InvalidOperationException(
                $"Table name '{tableName}' is {bytes} bytes; PostgreSQL's identifier limit is "
                + $"{MaxIdentifierBytes}. The fixed part of a leaf name of table '{settings.FullName}' is "
                + $"'{settings.DatabaseTableName}' ({Encoding.UTF8.GetByteCount(settings.DatabaseTableName)} bytes) "
                + $"plus the range suffix '{suffix}' ({Encoding.UTF8.GetByteCount(suffix)} bytes), i.e. "
                + $"{fixedBytes}; every partition key value costs its own bytes plus "
                + $"'{settings.SqlPartSeparator}' ({Encoding.UTF8.GetByteCount(settings.SqlPartSeparator)} bytes). "
                + "Shorten the partition key values (tenant, message part, consumer group) or configure a "
                + "shorter table name.");
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
