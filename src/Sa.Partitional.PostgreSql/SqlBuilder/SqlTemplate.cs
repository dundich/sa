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
    public static string CreateNestedSql(this ITableSettings settings, StrOrNum[] values) =>
$"""


-- NESTED PARTITION

CREATE TABLE IF NOT EXISTS {settings.GetQualifiedTableName(values)}
PARTITION OF {settings.GetQualifiedTableName(values[0..^1])}
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
        string timeRangeTablename = settings.GetQualifiedTableName(date, values);
        LimSection<DateTimeOffset> range = settings.PartBy.GetRange(date);
        string cacheTablename = settings.GetCacheByRangeTableName();
        var partValues = StrOrNumsToFmtString(values);

        return
$"""


-- ({settings.PartByRangeFieldName})  part by: {settings.PartBy}

CREATE TABLE IF NOT EXISTS {timeRangeTablename}
PARTITION OF {settings.GetQualifiedTableName(values)}
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
  to_date bigint NOT NULL
)
;

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
WHERE root = {QuoteLiteral(settings.FullName)} AND from_date >= @from_date
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
WHERE root = {QuoteLiteral(settings.FullName)} AND to_date <= @to_date
ORDER BY from_date ASC
;
""";
    }


    public static string SelectPartsQualifiedTablesSql(this ITableSettings settings, StrOrNum[] values)
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

    public static string GetQualifiedTableName(this ITableSettings settings, DateTimeOffset? date, StrOrNum[] values)
        => date != null
        ? settings.GetQualifiedTableName([.. values, settings.PartBy.Fmt(date.Value)])
        : settings.GetQualifiedTableName(values);


    private static string GetCacheByRangeTableName(this ITableSettings settings)
        => settings.GetQualifiedTableName(settings.PartTablePostfix);


    static string GetPartTableName(this ITableSettings settings, params StrOrNum[] values)
      => values.Length > 0
        ? $"{settings.DatabaseTableName}{settings.SqlPartSeparator}{values.JoinByString(settings.SqlPartSeparator)}"
        : $"{settings.DatabaseTableName}"
        ;

    static string GetQualifiedTableName(this ITableSettings settings, params StrOrNum[] values)
    {
        string tableName = settings.GetPartTableName(values);

        // 63 BYTES, not 63 chars: PostgreSQL truncates on the UTF-8 encoding.
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
