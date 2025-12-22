using Sa.Classes;
using Sa.Partitional.PostgreSql.Settings;

namespace Sa.Partitional.PostgreSql.Configuration.Builder;

internal sealed class TableBuilder(string schemaName, string tableName) : ITableBuilder
{
    static class Default
    {
        public readonly static PgPartBy DefaultPartBy = PgPartBy.Day;
        public const string PartByRangeFieldName = "created_at";
        public const string SqlPartSeparator = "__";
        public const int FillFactor = 0;
        public const string PartTablePostfix = "part$";
    }


    private readonly List<string> _fields = [];
    private readonly List<string> _parts = [];

    private readonly List<StrOrNum[]> _migrationPartValues = [];
    private IPartTableMigrationSupport? _migrationSupport;
    private Func<CancellationToken, Task<StrOrNum[][]>>? _getPartValues;

    private string? _timestamp;
    private PgPartBy? _partBy;
    private string? _separator = null;
    private string? _postfix = null;
    private int? _fillFactor = null;

    private Func<string>? _postSql = null;
    private Func<string>? _pkSql = null;

    public ITableBuilder AddFields(params string[] sqlFields)
    {
        ArgumentNullException.ThrowIfNull(sqlFields);

        if (sqlFields.Length == 0) throw new ArgumentException("fields is empty", nameof(sqlFields));
        _fields.AddRange(sqlFields);
        return this;
    }

    public ITableBuilder PartByList(params string[] fieldNames)
    {
        ArgumentNullException.ThrowIfNull(fieldNames);

        _parts.AddRange(fieldNames);
        return this;
    }

    public ITableBuilder TimestampAs(string timestampFieldName)
    {
        _timestamp = timestampFieldName;
        return this;
    }

    public ITableBuilder PartByRange(PgPartBy partBy, string? timestampFieldName = null)
    {
        _partBy = partBy;
        _timestamp ??= timestampFieldName;
        return this;
    }

    public ITableBuilder WithPartSeparator(string partSeparator)
    {
        _separator = partSeparator;
        return this;
    }

    public ITableBuilder WithFillFactor(int fillFactor)
    {
        _fillFactor = Math.Max(100, fillFactor);
        return this;
    }

    public ITableBuilder AddPostSql(Func<string> postSql)
    {
        _postSql = postSql ?? throw new ArgumentNullException(nameof(postSql));
        return this;
    }

    public ITableBuilder AddConstraintPkSql(Func<string> pkSql)
    {
        _pkSql = pkSql ?? throw new ArgumentNullException(nameof(pkSql));
        return this;
    }

    public ITableBuilder WithPartTablePostfix(string postfix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameof(postfix));

        _postfix = postfix;
        return this;
    }

    public ITableSettings Build()
    {
        var databaseTableName = tableName.Trim('"');
        var timestampField = _timestamp ?? Default.PartByRangeFieldName;
        string[] partByListFieldNames = [.. _parts];

        var firstIdSql = _fields.Find(c => !string.IsNullOrWhiteSpace(c));
        var idFieldName = firstIdSql?.Trim().Split(' ')[0] ?? string.Empty;

        return new TableSettings(
            DatabaseSchemaName: schemaName,
            DatabaseTableName: databaseTableName,
            FullName: $@"{schemaName}.{databaseTableName}",

            IdFieldName: idFieldName,
            Fields: [.. _fields],

            PartBy: _partBy ?? Default.DefaultPartBy,
            Migration: new PartTableMigrationSupport(_migrationPartValues, _getPartValues, _migrationSupport),

            PartByRangeFieldName: timestampField,
            PartByListFieldNames: partByListFieldNames,
            PartitionByFieldName: partByListFieldNames.Length == 0 ? timestampField : partByListFieldNames[0],

            SqlPartSeparator: _separator ?? Default.SqlPartSeparator,
            PostRootSql: _postSql,
            ConstraintPkSql: _pkSql,

            FillFactor: _fillFactor ?? Default.FillFactor,
            PartTablePostfix: _postfix ?? Default.PartTablePostfix
        );
    }

    public ITableBuilder AddMigration(params StrOrNum[] partValues)
    {
        _migrationPartValues.Add(partValues);
        return this;
    }

    public ITableBuilder AddMigration(Func<CancellationToken, Task<StrOrNum[][]>> getPartValues)
    {
        _getPartValues = getPartValues;
        return this;
    }

    public ITableBuilder AddMigration(IPartTableMigrationSupport migrationSupport)
    {
        _migrationSupport = migrationSupport;
        return this;
    }

    internal class PartTableMigrationSupport(
        IReadOnlyCollection<StrOrNum[]>? partValues, 
        Func<CancellationToken, Task<StrOrNum[][]>>? getPartValues, 
        IPartTableMigrationSupport? original) : IPartTableMigrationSupport
    {
        public async Task<StrOrNum[][]> GetParts(CancellationToken cancellationToken)
        {
            List<StrOrNum[]> result = partValues != null ? [.. partValues] : [];

            if (getPartValues != null)
            {
                StrOrNum[][] partItems = await getPartValues(cancellationToken);
                result.AddRange(partItems);
            }

            if (original != null)
            {
                StrOrNum[][] partItems = await original.GetParts(cancellationToken);
                result.AddRange(partItems);
            }

            return [.. result];
        }
    }
}
