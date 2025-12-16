using System.Diagnostics;

namespace Sa.Partitional.PostgreSql.Settings;

[DebuggerDisplay("settings root = {DatabaseTableName}")]
internal sealed record TableSettings(
    string DatabaseSchemaName,
    string DatabaseTableName,
    string FullName,

    string IdFieldName,
    string[] Fields,

    PgPartBy PartBy,
    IPartTableMigrationSupport Migration,

    string PartByRangeFieldName,
    string[] PartByListFieldNames,
    string PartitionByFieldName,

    string SqlPartSeparator,

    Func<string>? PostRootSql,
    Func<string>? ConstraintPkSql,
    int? FillFactor,
    string PartTablePostfix
)
: ITableSettings;
