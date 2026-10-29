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
    private RangeKeyType? _rangeKey;
    private Func<PartitionNameContext, string>? _longPartitionName;
    private TableRootMode _rootMode = TableRootMode.Create;
    private ConversionBackupMode _conversionBackup = ConversionBackupMode.Keep;
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

        if (_rootMode == TableRootMode.Adopt)
        {
            throw new InvalidOperationException(
                $"Table '{schemaName}.{tableName}' is declared with AdoptTable. Its fields describe the "
                + "existing table and are supplied in the AdoptTable call; do not add them separately.");
        }

        _fields.AddRange(sqlFields);
        return this;
    }

    /// <summary>
    /// Switches the table to <see cref="TableRootMode.Adopt"/> and records the fields that describe
    /// the existing plain table.
    /// </summary>
    /// <remarks>
    /// Registering the same table twice with the same field list is idempotent — the module's builder
    /// already merges repeated declarations, and repeating an identical adopt call must stay a
    /// no-op. A different field list (or a table already declared with fields through
    /// <c>AddTable</c>) is a configuration error and fails here, while the offending declaration is
    /// still identifiable.
    /// </remarks>
    internal TableBuilder DeclareAdopt(string[] sqlFields)
    {
        if (sqlFields.Length == 0)
        {
            throw new ArgumentException(
                $"Table '{schemaName}.{tableName}' is adopted, so its fields are required: they describe "
                + "the existing table and are used to build the partitioned root.",
                nameof(sqlFields));
        }

        if (_rootMode == TableRootMode.Adopt)
        {
            if (!_fields.SequenceEqual(sqlFields, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Table '{schemaName}.{tableName}' is adopted twice with different fields. "
                    + "Keep a single declaration, or make the repeated one identical.");
            }

            return this;
        }

        if (_fields.Count > 0)
        {
            throw new InvalidOperationException(
                $"Table '{schemaName}.{tableName}' is already declared with fields (AddTable/CreateTable) "
                + "and cannot be adopted as well. Use either AddTable (the library creates the table) "
                + "or AdoptTable (the table already exists).");
        }

        _rootMode = TableRootMode.Adopt;
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

    public ITableBuilder WithRangeKey(RangeKeyType rangeKeyType)
    {
        _rangeKey = rangeKeyType;
        return this;
    }

    public ITableBuilder WithLongPartitionName(Func<PartitionNameContext, string> factory)
    {
        _longPartitionName = factory ?? throw new ArgumentNullException(nameof(factory));
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

    public ITableBuilder WithConversionBackup(ConversionBackupMode mode)
    {
        _conversionBackup = mode;
        return this;
    }

    public ITableSettings Build()
    {
        var databaseTableName = tableName.Trim('"');
        var timestampField = _timestamp ?? Default.PartByRangeFieldName;
        string[] partByListFieldNames = [.. _parts];

        string idFieldName = GetIdFieldName();
        string partTablePostfix = _postfix ?? Default.PartTablePostfix;

        ValidatePartitionColumns(partByListFieldNames);

        RangeKeyType rangeKeyType = ResolveRangeKeyType(timestampField);

        if (!SqlFieldName.HasColumn(_fields, timestampField) && rangeKeyType != RangeKeyType.UnixSeconds)
        {
            throw new InvalidOperationException(
                $"Table '{schemaName}.{tableName}' selects the '{rangeKeyType}' range key, but its range "
                + $"column '{timestampField}' is not declared. The library only adds a bigint column "
                + "itself; declare the timestamp/date column explicitly.");
        }

        // A table that already exists cannot be extended by the library, so the range column has to
        // be part of it. For a table the library creates itself the column is added on the fly.
        if (_rootMode == TableRootMode.Adopt && !SqlFieldName.HasColumn(_fields, timestampField))
        {
            throw new InvalidOperationException(
                $"Table '{schemaName}.{tableName}' is adopted, but its range partition column "
                + $"'{timestampField}' is not declared among its fields. An existing table cannot be "
                + "extended by the library, so declare the column in the AdoptTable call.");
        }

        if (_rootMode == TableRootMode.Adopt)
        {
            string? inlinePk = _fields.FirstOrDefault(field =>
                field.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase));

            if (inlinePk is not null)
            {
                throw new InvalidOperationException(
                    $"Table '{schemaName}.{tableName}' declares an inline PRIMARY KEY ('{inlinePk}'). "
                    + "AdoptTable generates its own primary-key constraint, which has to include the "
                    + "partition key; remove the clause from the field declaration.");
            }
        }

        // A root name PostgreSQL would truncate silently makes every generated identifier (cache
        // table, partitions, cache rows) refer to a name that does not exist. Fail while the
        // configuration is still identifiable.
        int rootBytes = Encoding.UTF8.GetByteCount(databaseTableName);
        if (rootBytes > MaxIdentifierBytes)
        {
            throw new InvalidOperationException(
                $"Root table name '{databaseTableName}' is {rootBytes} bytes; PostgreSQL's identifier "
                + $"limit is {MaxIdentifierBytes}. It is over by {rootBytes - MaxIdentifierBytes} byte(s). "
                + "Shorten the table name in the schema declaration.");
        }

        int schemaBytes = Encoding.UTF8.GetByteCount(schemaName);
        if (schemaBytes > MaxIdentifierBytes)
        {
            throw new InvalidOperationException(
                $"Schema name '{schemaName}' is {schemaBytes} bytes; PostgreSQL's identifier limit is "
                + $"{MaxIdentifierBytes}. It is over by {schemaBytes - MaxIdentifierBytes} byte(s).");
        }

        // Fail fast on a name PostgreSQL would silently truncate: the truncated DDL would never
        // match the identifiers written to the cache table. The name carrying partition values
        // cannot be checked here - those values are only known when the DDL is generated, and
        // SqlTemplate.GetQualifiedTableName checks them there.
        string cacheTableName = $"{databaseTableName}{_separator ?? Default.SqlPartSeparator}{partTablePostfix}";
        int cacheBytes = Encoding.UTF8.GetByteCount(cacheTableName);
        if (cacheBytes > MaxIdentifierBytes)
        {
            throw new InvalidOperationException(
                $"Cache table name '{cacheTableName}' of table '{schemaName}.{databaseTableName}' is "
                + $"{cacheBytes} bytes; PostgreSQL's identifier limit is {MaxIdentifierBytes}. It is over by "
                + $"{cacheBytes - MaxIdentifierBytes} byte(s). Shorten the table name, the separator "
                + $"(WithPartSeparator) or the cache postfix (WithPartTablePostfix, currently "
                + $"'{partTablePostfix}').");
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
            RangeKeyType: rangeKeyType,
            LongPartitionName: _longPartitionName,
            PartByListFieldNames: partByListFieldNames,
            PartitionByFieldName: partByListFieldNames.Length == 0 ? timestampField : partByListFieldNames[0],

            RootMode: _rootMode,
            ConversionBackup: _conversionBackup,
            SqlPartSeparator: _separator ?? Default.SqlPartSeparator,
            PostRootSql: _postSql,
            ConstraintPkSql: _pkSql,

            FillFactor: _fillFactor,
            PartTablePostfix: partTablePostfix,

            DefaultPartition: _defaultPartition
        );
    }

    /// <summary>
    /// Validates the lists columns the partitioning DDL depends on.
    /// </summary>
    /// <remarks>
    /// A list-partition column that no field declares (<c>column "x" named in partition key does not
    /// exist</c>), or that is listed twice (<c>PARTITION BY LIST ("x")</c> twice at consecutive
    /// levels), is otherwise reported by PostgreSQL at migration time - from a background job, with
    /// a message that does not name the table definition that caused it.
    /// </remarks>
    private void ValidatePartitionColumns(string[] partByListFieldNames)
    {
        var duplicates = partByListFieldNames
            .GroupBy(c => c, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Table '{schemaName}.{tableName}' declares the list partition column(s) "
                + $"{string.Join(", ", duplicates)} more than once. Every list level partitions on a "
                + "distinct column.");
        }

        foreach (string listField in partByListFieldNames)
        {
            if (SqlFieldName.HasColumn(_fields, listField)) continue;

            throw new InvalidOperationException(
                $"Table '{schemaName}.{tableName}' partitions by list on '{listField}', but no field "
                + "declares that column. Add it with AddFields/AddTable - PostgreSQL would reject the "
                + "generated PARTITION BY LIST otherwise.");
        }
    }

    /// <summary>
    /// Resolves the type of the range key from the declared range column.
    /// </summary>
    /// <remarks>
    /// A table whose range column is not declared has the column added by the library as
    /// <c>bigint</c>, so <see cref="RangeKeyType.UnixSeconds"/> is the answer. An explicitly declared
    /// column selects the matching type; the explicit <see cref="WithRangeKey"/> override wins for a
    /// domain/alias the detector does not know.
    /// </remarks>
    private RangeKeyType ResolveRangeKeyType(string timestampField)
    {
        if (_rangeKey is { } explicitKey) return explicitKey;

        string? rangeField = _fields.FirstOrDefault(field =>
            SqlFieldName.Extract(field).SequenceEqual(timestampField));

        if (rangeField is null) return RangeKeyType.UnixSeconds;

        string type = SqlFieldName.ExtractType(rangeField).ToString().ToLowerInvariant();
        string declaration = rangeField.ToLowerInvariant();

        if (type is "bigint" or "int8")
        {
            return RangeKeyType.UnixSeconds;
        }

        if (type is "timestamptz" || declaration.Contains("with time zone", StringComparison.Ordinal))
        {
            return RangeKeyType.TimestampTz;
        }

        if (type.StartsWith("timestamp", StringComparison.Ordinal))
        {
            return RangeKeyType.Timestamp;
        }

        if (type is "date")
        {
            return RangeKeyType.Date;
        }

        throw new InvalidOperationException(
            $"Table '{schemaName}.{tableName}' declares its range partition column '{timestampField}' "
            + $"as '{type}', which is not a supported range key. Supported types are bigint (Unix "
            + "seconds), timestamptz, timestamp [with|without time zone] and date. Use WithRangeKey(...) "
            + "to override the auto-detected type when the column is declared through a domain.");
    }

    /// <summary>
    /// The first declared column doubles as the primary key id column, so its name has to be
    /// extracted from the raw SQL definition: the name is the leading token (quotes included when the
    /// identifier contains a space) and is dropped into the settings unquoted, because it is quoted
    /// again by <c>QuoteIdentifier</c>.
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
