using Sa.Partitional.PostgreSql.SqlBuilder;
using Sa.Partitional.PostgreSql.Classes;
using Sa.Partitional.PostgreSql.Settings;
using System.Text;

namespace Sa.Partitional.PostgreSql.Configuration.Builder;

internal sealed class TableBuilder(string schemaName, string tableName) : ITableBuilder
{
    static class Default
    {
        public readonly static PgPartBy DefaultPartBy = PgPartBy.Day;
        public const string PartByRangeFieldName = "created_at";
        public const string SqlPartSeparator = "__";
        public const string PartTablePostfix = "part$";
    }

    /// <summary>
    /// PostgreSQL truncates identifiers to 63 <b>bytes</b> (UTF-8), not to 63 characters.
    /// </summary>
    private const int MaxIdentifierBytes = 63;

    /// <summary>PostgreSQL's accepted range for the <c>fillfactor</c> storage parameter.</summary>
    private const int MinFillFactor = 10;
    private const int MaxFillFactor = 100;


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
    private bool _defaultPartition = false;

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

        // TimestampAs is the more specific declaration, so it wins: the argument here is only a
        // fallback for callers that do not call TimestampAs at all.
        _timestamp ??= timestampFieldName;
        return this;
    }

    public ITableBuilder WithPartSeparator(string partSeparator)
    {
        // Same validation as WithPartTablePostfix: an empty separator silently glues the values
        // together ("ab" instead of "a" + sep + "b"), which collapses distinct partitions onto one
        // table name, and a quote would have to be escaped in every generated identifier.
        ArgumentException.ThrowIfNullOrWhiteSpace(partSeparator);

        if (partSeparator.Contains('"'))
        {
            throw new ArgumentException("Part separator cannot contain a double quote.", nameof(partSeparator));
        }

        // A control character would end up inside every generated identifier. It is legal there, but
        // it can never be intended, and it makes the DDL and the partition names unreadable in logs.
        if (partSeparator.Any(char.IsControl))
        {
            throw new ArgumentException("Part separator cannot contain control characters.", nameof(partSeparator));
        }

        _separator = partSeparator;
        return this;
    }

    public ITableBuilder WithFillFactor(int fillFactor)
    {
        // PostgreSQL's own range for fillfactor is 10–100. Clamping silently created the table with a
        // value nobody asked for; a value outside the range would otherwise only surface as an error
        // from CREATE TABLE. Reject it here, where the offending setting is still identifiable.
        if (fillFactor is < MinFillFactor or > MaxFillFactor)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fillFactor), fillFactor,
                $"FillFactor must be between {MinFillFactor} and {MaxFillFactor} (PostgreSQL constraint).");
        }

        _fillFactor = fillFactor;
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
        // nameof(postfix) is the constant "postfix", so the check below never fired and a null /
        // empty postfix silently produced a cache table named exactly like the root table.
        ArgumentException.ThrowIfNullOrWhiteSpace(postfix);

        // The postfix becomes part of the cache table's identifier. A quote or a control character
        // there is never intended, and it would make every generated name unreadable.
        if (postfix.Contains('"') || postfix.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Part table postfix cannot contain a double quote or control characters.", nameof(postfix));
        }

        _postfix = postfix;
        return this;
    }

    public ITableBuilder WithDefaultPartition()
    {
        _defaultPartition = true;
        return this;
    }

    public ITableSettings Build()
    {
        var databaseTableName = tableName.Trim('"');
        var timestampField = _timestamp ?? Default.PartByRangeFieldName;
        string[] partByListFieldNames = [.. _parts];

        string idFieldName = GetIdFieldName();
        string partTablePostfix = _postfix ?? Default.PartTablePostfix;

        // Fail fast on a name PostgreSQL would silently truncate: the truncated DDL would never
        // match the identifiers written to the cache table. The name carrying partition values
        // cannot be checked here - those values are only known when the DDL is generated, and
        // SqlTemplate.GetQualifiedTableName checks them there.
        string cacheTableName = $"{databaseTableName}{_separator ?? Default.SqlPartSeparator}{partTablePostfix}";
        if (Encoding.UTF8.GetByteCount(cacheTableName) > MaxIdentifierBytes)
        {
            throw new InvalidOperationException(
                $"Cache table name '{cacheTableName}' of table '{schemaName}.{databaseTableName}' exceeds "
                + $"PostgreSQL's {MaxIdentifierBytes}-byte identifier limit.");
        }

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

            FillFactor: _fillFactor,
            PartTablePostfix: partTablePostfix,

            DefaultPartition: _defaultPartition
        );
    }

    /// <summary>
    /// The first declared column doubles as the primary key id column, so its name has to be
    /// extracted from the raw SQL definition: cut at the first whitespace and drop the surrounding
    /// quotes, because the value is quoted again by <c>QuoteIdentifier</c>.
    /// </summary>
    private string GetIdFieldName()
    {
        string? firstField = _fields.Find(c => !string.IsNullOrWhiteSpace(c));

        if (firstField is null)
        {
            throw new InvalidOperationException(
                $"Table '{schemaName}.{tableName}' declares no fields. Call AddFields (or AddTable) "
                + "with at least one column definition - the first one becomes the primary key id column.");
        }

        string idFieldName = SqlFieldName.Extract(firstField).ToString();

        if (idFieldName.Length == 0)
        {
            throw new InvalidOperationException(
                $"Table '{schemaName}.{tableName}' has an empty first field definition ('{firstField}').");
        }

        return idFieldName;
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
                StrOrNum[][] partItems = await getPartValues(cancellationToken).ConfigureAwait(false);
                result.AddRange(partItems);
            }

            if (original != null)
            {
                StrOrNum[][] partItems = await original.GetParts(cancellationToken).ConfigureAwait(false);
                result.AddRange(partItems);
            }

            return [.. result];
        }
    }
}
