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
    RangeKeyType RangeKeyType,
    Func<PartitionNameContext, string>? LongPartitionName,
    string[] PartByListFieldNames,
    string PartitionByFieldName,

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
