using System.Diagnostics;

namespace Sa.Partitional.PostgreSql.Settings;

[DebuggerDisplay("settings root = {DatabaseTableName}")]
internal sealed record TableSettings(
    string DatabaseSchemaName,
    string DatabaseTableName,
    string FullName,

    string IdFieldName,
    IReadOnlyList<string> Fields,

    PgPartBy PartBy,
    IPartTableMigrationSupport Migration,

    string PartByRangeFieldName,
    RangeKeyType RangeKeyType,
    Func<PartitionNameContext, string>? LongPartitionName,
    IReadOnlyList<string> PartByListFieldNames,

    TableRootMode RootMode,
    ConversionBackupMode ConversionBackup,
    string SqlPartSeparator,

    Func<string>? PostRootSql,
    Func<string>? ConstraintPkSql,
    int? FillFactor,
    string PartTablePostfix,

    bool DefaultPartition
)
: ITableSettings;
