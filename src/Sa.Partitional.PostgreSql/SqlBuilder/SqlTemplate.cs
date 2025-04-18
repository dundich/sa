using Sa.Classes;
using Sa.Extensions;

namespace Sa.Partitional.PostgreSql.SqlBuilder;

internal static class SqlTemplate
{
    const string CacheByRangeTableNamePostfix = "$part";

    private const char NumOrStrSplitter = ',';

    public static string CreateRootSql(this ITableSettings settings)
    {
        string pkList = settings.PartByListFieldNames.Contains(settings.IdFieldName)
            ? settings.PartByListFieldNames.JoinByString(",")
            : new string[] { settings.IdFieldName }.Concat(settings.PartByListFieldNames).JoinByString(",")
            ;

        return $"""

CREATE SCHEMA IF NOT EXISTS {settings.DatabaseSchemaName};

CREATE TABLE IF NOT EXISTS {settings.GetQualifiedTableName()} (
    {settings.Fields.JoinByString($",{Environment.NewLine}    ")},
    {settings.PartByRangeFieldName} bigint NOT NULL,
    CONSTRAINT "{settings.Pk()}" PRIMARY KEY ({pkList},{settings.PartByRangeFieldName})
) {settings.GetPartitionalSql(0)}
;

-- post sql
{settings.PostRootSql?.Invoke()}

""";
    }

    public static string CreateNestedSql(this ITableSettings settings, StrOrNum[] values) =>
$"""


-- {settings.GetPartitionalSql(values.Length - 1)[18..]}

CREATE TABLE IF NOT EXISTS {settings.GetQualifiedTableName(values)}
PARTITION OF {settings.GetQualifiedTableName(values[0..^1])}
FOR VALUES IN ({values[^1].Match(s => $"'{s}'", n => n.ToString())})
{settings.GetPartitionalSql(values.Length)}
;
""";

    public static string CreatePartByRangeSql(this ITableSettings settings, DateTimeOffset date, StrOrNum[] values)
    {
        string timeRangeTablename = settings.GetQualifiedTableName(date, values);
        LimSection<DateTimeOffset> range = settings.PartBy.GetRange(date);
        string cacheTablename = settings.GetCacheByRangeTableName();

        return
$"""


-- ({settings.PartByRangeFieldName})  part by: {settings.PartBy}

CREATE TABLE IF NOT EXISTS {timeRangeTablename}
PARTITION OF {settings.GetQualifiedTableName(values)}
FOR VALUES FROM ({range.Start.ToUnixTimeSeconds()}) TO ({range.End.ToUnixTimeSeconds()}) 
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
VALUES ('{timeRangeTablename}','{settings.FullName}','{StrOrNumsToFmtString(values)}','{settings.PartBy.Name}',{range.Start.ToUnixTimeSeconds()},{range.End.ToUnixTimeSeconds()}) 
ON CONFLICT (id) DO NOTHING
;

"""
        ;
    }

    public static string SelectPartsFromDateSql(this ITableSettings settings)
    {
        return
$"""
SELECT id,root,part_values,part_by,from_date
FROM {settings.GetCacheByRangeTableName()} 
WHERE root = '{settings.FullName}' AND from_date >= @from_date
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
WHERE 
    root = '{settings.FullName}' 
    AND to_date <= @to_date
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
    WHERE inhparent = '{qualifiedTableName}'::regclass
)
SELECT pt::text from pt
;
""";


    public static string DropPartSql(this ITableSettings settings, string qualifiedTableName)
    {
        return $"""
DROP TABLE IF EXISTS {qualifiedTableName};
DELETE FROM {settings.GetCacheByRangeTableName()} WHERE id='{qualifiedTableName}'; 
""";
    }

    public static string GetQualifiedTableName(this ITableSettings settings, DateTimeOffset? date, StrOrNum[] values)
        => date != null
        ? settings.GetQualifiedTableName([.. values, settings.PartBy.Fmt(date.Value)])
        : settings.GetQualifiedTableName(values);


    private static string GetCacheByRangeTableName(this ITableSettings settings) => settings.GetQualifiedTableName(CacheByRangeTableNamePostfix);


    static string GetQualifiedTableName(this ITableSettings settings, params StrOrNum[] values)
        => values.Length > 0
        ? $"{settings.DatabaseSchemaName}.\"{settings.DatabaseTableName}{settings.SqlPartSeparator}{values.JoinByString(settings.SqlPartSeparator)}\""
        : $"{settings.DatabaseSchemaName}.\"{settings.DatabaseTableName}\""
        ;

    static string GetPartitionalSql(this ITableSettings settings, int partIndex)
        => partIndex >= 0 && partIndex < settings.PartByListFieldNames.Length
            ? $"PARTITION BY LIST ({settings.PartByListFieldNames[partIndex]})"
            : $"PARTITION BY RANGE ({settings.PartByRangeFieldName})"
            ;

    static string Pk(this ITableSettings settings) => settings.ConstraintPkSql?.Invoke() ?? $"pk_{settings.DatabaseTableName}";


    internal static StrOrNum[] ParseStrOrNums(string fmtInput) => [.. fmtInput
            .Split(NumOrStrSplitter, StringSplitOptions.RemoveEmptyEntries)
            .Select(StrOrNum.FromFmtStr)];

    private static string StrOrNumsToFmtString(StrOrNum[] input)
        => string.Join(NumOrStrSplitter, input.Select(c => c.ToFmtString()));

}
