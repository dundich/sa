using System.Diagnostics;

namespace Sa.Partitional.PostgreSql.Settings;

[DebuggerDisplay("settings root = {DatabaseTableName}")]
internal class TableSettings : ITableSettings
{
    static class Default
    {
        public readonly static PgPartBy DefaultPartBy = PgPartBy.Day;
        public const string PartByRangeFieldName = "created_at";
        public const string SqlPartSeparator = "__";
    }

    public TableSettings(
        string schemaName
        , string tableName
        , string[] fields
        , string[] partByListFields
        , IPartTableMigrationSupport migration
        , PgPartBy? partByRange
        , Func<string>? postRootSql = null
        , Func<string>? constraintPkSql = null
        , string? timestampField = null
        , string? sqlPartSeparator = null
    )
    {
        DatabaseSchemaName = schemaName;
        DatabaseTableName = tableName.Trim('"');
        Fields = fields;
        Migration = migration;

        PartBy = partByRange ?? Default.DefaultPartBy;

        PartByListFieldNames = partByListFields;
        PartByRangeFieldName = timestampField ?? Default.PartByRangeFieldName;
        PartitionByFieldName = PartByListFieldNames.Length == 0 ? PartByRangeFieldName : partByListFields[0];

        PostRootSql = postRootSql;
        ConstraintPkSql = constraintPkSql;

        IdFieldName = GetIdName(Array.Find(fields, c => !string.IsNullOrWhiteSpace(c)));

        FullName = $@"{DatabaseSchemaName}.{DatabaseTableName}";
        SqlPartSeparator = sqlPartSeparator ?? Default.SqlPartSeparator;
    }

    public string IdFieldName { get; }
    public string[] Fields { get; }
    public IPartTableMigrationSupport Migration { get; }

    public PgPartBy PartBy { get; }

    public string DatabaseSchemaName { get; }
    public string DatabaseTableName { get; }
    public string PartByRangeFieldName { get; }
    public string[] PartByListFieldNames { get; }
    public string PartitionByFieldName { get; }
    public string FullName { get; }
    public string SqlPartSeparator { get; }


    // extensions
    public Func<string>? PostRootSql { get; }
    public Func<string>? ConstraintPkSql { get; }

    static string GetIdName(string? idSql) => idSql?.Trim().Split(' ')[0] ?? string.Empty;

}
