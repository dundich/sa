using Microsoft.Extensions.Logging;
using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Partitional.PostgreSql.Classes;
using Sa.Partitional.PostgreSql.SqlBuilder;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Sa.Partitional.PostgreSql.Repositories;

/// <summary>
/// Converts an existing plain table into a partitioned one the first time the library meets it.
/// </summary>
internal interface IPartTableAdopter
{
    /// <summary>
    /// Ensures that <paramref name="tableName"/> is a partitioned table managed by this library.
    /// Tables in <see cref="TableRootMode.Create"/> are ignored; adopted tables are converted exactly
    /// once per process.
    /// </summary>
    Task EnsureAdopted(string tableName, CancellationToken cancellationToken);
}

/// <summary>
/// Adopts existing plain tables: renames the original table to a backup, creates the partitioned
/// root declared by its settings, creates the partitions needed by the existing rows and copies them
/// across — all inside one transaction, so a failed run leaves the original table untouched.
/// </summary>
/// <remarks>
/// <para>
/// Only tables without partitions are supported. The state is resolved in three steps: a missing
/// table is an error, a partitioned table is accepted only when the library's cache table is present
/// (that is what marks it as already adopted — otherwise it is somebody else's partitioned table),
/// and a plain table is converted.
/// </para>
/// <para>
/// The conversion runs under a transaction-scoped advisory lock, so two hosts starting at the same
/// time cannot convert the same table twice; the losers wait and then observe the partitioned root.
/// </para>
/// </remarks>
internal sealed partial class PartTableAdopter(
    IPgDataSource dataSource,
    ISqlBuilder sqlBuilder,
    ILogger<PartTableAdopter> logger) : IPartTableAdopter
{
    /// <summary>Upper bound on partitions created for a single adopted table.</summary>
    /// <remarks>
    /// A range column holding very old values (or a mistake such as Unix milliseconds) would
    /// otherwise ask for hundreds of thousands of tables and turn a startup migration into an
    /// accidental denial of service. The limit is deliberately generous for real tables and fail-fast
    /// for a wrong key.
    /// </remarks>
    private const int MaxPartitionsPerConversion = 1000;

    private const int MaxIdentifierBytes = 63;

    /// <summary>Tables already converted by this process, keyed by full name.</summary>
    private readonly ConcurrentDictionary<string, bool> _adopted = new(StringComparer.Ordinal);

    /// <summary>One gate per table so concurrent callers share a single conversion.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public async Task EnsureAdopted(string tableName, CancellationToken cancellationToken)
    {
        ISqlTableBuilder? builder = sqlBuilder[tableName];

        if (builder is null || builder.Settings.RootMode != TableRootMode.Adopt) return;

        string key = builder.FullName;
        if (_adopted.ContainsKey(key)) return;

        SemaphoreSlim gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_adopted.ContainsKey(key)) return;

            await Adopt(builder, cancellationToken).ConfigureAwait(false);
            _adopted[key] = true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task Adopt(ISqlTableBuilder builder, CancellationToken cancellationToken)
    {
        ITableSettings settings = builder.Settings;
        string qualifiedTable = SqlTemplate.QualifiedName(settings.DatabaseSchemaName, settings.DatabaseTableName);
        string qualifiedCache = CacheTableName(settings);

        TableState state = await ReadState(qualifiedTable, qualifiedCache, null, cancellationToken).ConfigureAwait(false);

        if (!state.Exists)
        {
            throw new InvalidOperationException(
                $"Table '{settings.FullName}' is declared with AdoptTable, but it does not exist. "
                + "AdoptTable takes over an existing plain table; use AddTable when the library should "
                + "create it.");
        }

        if (state.IsPartitioned)
        {
            if (!state.CacheExists)
            {
                throw new InvalidOperationException(
                    $"Table '{settings.FullName}' is already partitioned, but it was not created by this "
                    + "library: its cache table is missing. AdoptTable supports only tables without "
                    + "partitions; partition ownership cannot be shared.");
            }

            await VerifyColumns(settings, qualifiedTable, null, cancellationToken).ConfigureAwait(false);
            LogAlreadyAdopted(settings.FullName);
            return;
        }

        if (!state.IsPlainTable)
        {
            throw new InvalidOperationException(
                $"Table '{settings.FullName}' exists but is not an ordinary table (relkind = '{state.RelKind}'). "
                + "AdoptTable supports ordinary, non-partitioned tables only.");
        }

        await ConvertToPartitioned(builder, qualifiedTable, qualifiedCache, cancellationToken).ConfigureAwait(false);
    }

    private async Task ConvertToPartitioned(
        ISqlTableBuilder builder,
        string qualifiedTable,
        string qualifiedCache,
        CancellationToken cancellationToken)
    {
        ITableSettings settings = builder.Settings;

        await using PgTransaction tx = await dataSource.BeginTransaction(cancellationToken).ConfigureAwait(false);

        await Execute(tx, "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0));",
            [new NpgsqlParameter<string>("key", settings.FullName)], cancellationToken).ConfigureAwait(false);

        // Re-read under the lock: another host may have converted the table while this one waited.
        TableState state = await ReadState(qualifiedTable, qualifiedCache, tx, cancellationToken).ConfigureAwait(false);

        if (state.IsPartitioned && state.CacheExists)
        {
            LogAlreadyAdopted(settings.FullName);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!state.IsPlainTable)
        {
            throw new InvalidOperationException(
                $"Table '{settings.FullName}' changed while the adoption lock was being acquired "
                + $"(relkind = '{state.RelKind}'). Retry the migration.");
        }

        // Incoming foreign keys and dependent views follow the renamed table, so they would silently
        // keep pointing at the backup. Refusing the conversion is the only outcome that does not
        // split the application between two tables.
        await EnsureNoDependents(tx, settings, qualifiedTable, cancellationToken).ConfigureAwait(false);

        string backupBare = await BackupName(tx, settings, cancellationToken).ConfigureAwait(false);
        string qualifiedBackup = SqlTemplate.QualifiedName(settings.DatabaseSchemaName, backupBare);

        LogConverting(settings.FullName, qualifiedBackup);

        await Execute(tx,
            $"ALTER TABLE {qualifiedTable} RENAME TO {SqlTemplate.QuoteIdentifier(backupBare)};",
            [], cancellationToken).ConfigureAwait(false);

        // The root DDL is the one normally emitted by Create mode: schema, root table, primary key,
        // optional DEFAULT partition and post-SQL hook.
        await Execute(tx, builder.CreateRootSql(), [], cancellationToken).ConfigureAwait(false);
        await Execute(tx, builder.CreateCacheSql(), [], cancellationToken).ConfigureAwait(false);

        int partitions = await CreatePartitionsForExistingRows(
            tx, builder, qualifiedBackup, cancellationToken).ConfigureAwait(false);

        await CopyRows(tx, settings, qualifiedTable, qualifiedBackup, cancellationToken).ConfigureAwait(false);

        await ResetIdentity(tx, settings, qualifiedTable, cancellationToken).ConfigureAwait(false);

        if (settings.ConversionBackup == ConversionBackupMode.Drop)
        {
            // Counts have already been verified at this point; dropping the original inside the same
            // transaction still rolls back with it if the commit fails.
            await Execute(tx, $"DROP TABLE {qualifiedBackup};", [], cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (settings.ConversionBackup == ConversionBackupMode.Drop)
        {
            LogConvertedWithoutBackup(settings.FullName, partitions);
        }
        else
        {
            LogConverted(settings.FullName, partitions, qualifiedBackup);
        }
    }

    /// <summary>
    /// Creates the partitions that cover the rows currently held by the backup table. List levels are
    /// resolved from the data itself when the configuration does not declare migration values.
    /// </summary>
    private async Task<int> CreatePartitionsForExistingRows(
        PgTransaction tx,
        ISqlTableBuilder builder,
        string qualifiedBackup,
        CancellationToken cancellationToken)
    {
        ITableSettings settings = builder.Settings;

        SourceColumns columns = await ReadColumns(tx, qualifiedBackup, cancellationToken).ConfigureAwait(false);

        if (!columns.Types.TryGetValue(settings.PartByRangeFieldName, out string? rangeType)
            || !IsRangeTypeCompatible(settings.RangeKeyType, rangeType))
        {
            throw new InvalidOperationException(
                $"Table '{settings.FullName}' cannot be partitioned by range on "
                + $"'{settings.PartByRangeFieldName}': its type is '{rangeType ?? "<missing>"}', which "
                + $"does not match the configured range key '{settings.RangeKeyType}'.");
        }

        (DateTimeOffset? min, DateTimeOffset? max) = await ReadRangeBounds(tx, settings, qualifiedBackup, cancellationToken).ConfigureAwait(false);

        // An empty table has no range to cover; the cache table created above is the adoption marker.
        if (min is null || max is null) return 0;

        List<StrOrNum[]> partValues = settings.PartByListFieldNames.Length == 0
            ? [[]]
            : await ReadListValues(tx, settings, qualifiedBackup, cancellationToken).ConfigureAwait(false);

        if (partValues.Count == 0)
        {
            throw new InvalidOperationException(
                $"Table '{settings.FullName}' is list-partitioned, but its rows produced no list values. "
                + "Nothing could be copied into the partitioned root.");
        }

        List<DateTimeOffset> dates = BuildDateRange(settings, min.Value, max.Value);

        int created = 0;

        foreach (StrOrNum[] values in partValues)
        {
            foreach (DateTimeOffset date in dates)
            {
                string sql = builder.CreateSql(date, values);
                await Execute(tx, sql, [], cancellationToken).ConfigureAwait(false);
                created++;
            }
        }

        return created;
    }

    private static List<DateTimeOffset> BuildDateRange(ITableSettings settings, DateTimeOffset min, DateTimeOffset max)
    {
        DateTimeOffset cursor = settings.PartBy.GetRange(min).Start;
        DateTimeOffset end = settings.PartBy.GetRange(max).End;

        List<DateTimeOffset> dates = [];

        while (cursor < end)
        {
            dates.Add(cursor);

            if (dates.Count > MaxPartitionsPerConversion)
            {
                throw new InvalidOperationException(
                    $"Table '{settings.FullName}' holds data covering more than {MaxPartitionsPerConversion} "
                    + "partitions. Check that the range partition column holds Unix seconds, not "
                    + "milliseconds, or reduce the data set before adopting the table.");
            }

            cursor = settings.PartBy.GetRange(cursor).End;
        }

        return dates;
    }

    /// <summary>
    /// Copies every row from the renamed original table into the partitioned root. The column list is
    /// explicit: an implicit <c>SELECT *</c> would silently rely on physical column order.
    /// </summary>
    private async Task CopyRows(
        PgTransaction tx,
        ITableSettings settings,
        string qualifiedTable,
        string qualifiedBackup,
        CancellationToken cancellationToken)
    {
        SourceColumns source = await ReadColumns(tx, qualifiedBackup, cancellationToken).ConfigureAwait(false);

        string[] target = settings.Fields
            .Select(field => SqlFieldName.Extract(field).ToString())
            .Where(name => name.Length > 0)
            .ToArray();

        if (target.Length == 0)
        {
            throw new InvalidOperationException(
                $"Table '{settings.FullName}' has no declared columns to copy.");
        }

        string[] missing = target.Where(column => !source.Names.Contains(column, StringComparer.Ordinal)).ToArray();
        string[] extra = source.Names.Where(column => !target.Contains(column, StringComparer.Ordinal)).ToArray();

        if (missing.Length > 0 || extra.Length > 0)
        {
            throw new InvalidOperationException(
                $"Table '{settings.FullName}' does not match its declared fields. "
                + $"Missing in the database: {Join(missing)}. Not declared: {Join(extra)}. "
                + "The column sets must match exactly, otherwise copying the rows would lose columns.");
        }

        string columns = string.Join(", ", target.Select(SqlTemplate.QuoteIdentifier));

        // OVERRIDING SYSTEM VALUE is only needed for GENERATED ALWAYS identity columns; emitting it
        // for a table without one is at best noise and is rejected by older servers.
        string overriding = source.HasAlwaysIdentity ? " OVERRIDING SYSTEM VALUE" : string.Empty;

        await Execute(tx,
            $"INSERT INTO {qualifiedTable} ({columns}){overriding} SELECT {columns} FROM {qualifiedBackup};",
            [], cancellationToken).ConfigureAwait(false);

        long backupRows = await ScalarLong(tx, $"SELECT count(*) FROM {qualifiedBackup};", [], cancellationToken).ConfigureAwait(false);
        long rootRows = await ScalarLong(tx, $"SELECT count(*) FROM {qualifiedTable};", [], cancellationToken).ConfigureAwait(false);

        if (backupRows != rootRows)
        {
            throw new InvalidOperationException(
                $"Table '{settings.FullName}' was not copied completely: {backupRows} row(s) in the "
                + $"original table, {rootRows} in the partitioned root. The transaction is rolled back.");
        }

        LogCopiedRows(settings.FullName, rootRows);
    }

    /// <summary>
    /// Moves the identity/serial sequence past the copied identifiers, so the next INSERT does not
    /// collide with existing rows.
    /// </summary>
    private async Task ResetIdentity(
        PgTransaction tx,
        ITableSettings settings,
        string qualifiedTable,
        CancellationToken cancellationToken)
    {
        string qualifiedName = SqlTemplate.QualifiedName(settings.DatabaseSchemaName, settings.DatabaseTableName);

        string? sequence = await ScalarString(tx,
            "SELECT pg_get_serial_sequence(@table, @column);",
            [
                new NpgsqlParameter<string>("table", qualifiedName),
                new NpgsqlParameter<string>("column", settings.IdFieldName),
            ],
            cancellationToken).ConfigureAwait(false);

        if (sequence is null) return;

        long? maxId = await ScalarLongNullable(tx,
            $"SELECT max({SqlTemplate.QuoteIdentifier(settings.IdFieldName)}) FROM {qualifiedTable};",
            [], cancellationToken).ConfigureAwait(false);

        if (maxId is null) return;

        await Execute(tx, "SELECT setval(@sequence::regclass, @value);",
            [
                new NpgsqlParameter<string>("sequence", sequence),
                new NpgsqlParameter<long>("value", maxId.Value),
            ],
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TableState> ReadState(
        string qualifiedTable,
        string qualifiedCache,
        PgTransaction? tx,
        CancellationToken cancellationToken)
    {
        const string sql = """
SELECT COALESCE((SELECT relkind::text FROM pg_class WHERE oid = to_regclass(@table)), ''),
       (to_regclass(@cache) IS NOT NULL);
""";

        List<TableState> rows = await Query(tx, sql,
            static reader => new TableState(reader.GetString(0), reader.GetBoolean(1)),
            [
                new NpgsqlParameter<string>("table", qualifiedTable),
                new NpgsqlParameter<string>("cache", qualifiedCache),
            ],
            cancellationToken).ConfigureAwait(false);

        return rows.Count > 0 ? rows[0] : default;
    }

    /// <summary>
    /// Verifies that an already partitioned root still exposes every declared column and that the
    /// range column matches the configured range key type.
    /// </summary>
    private async Task VerifyColumns(
        ITableSettings settings,
        string qualifiedTable,
        PgTransaction? tx,
        CancellationToken cancellationToken)
    {
        SourceColumns columns = await ReadColumns(tx, qualifiedTable, cancellationToken).ConfigureAwait(false);

        foreach (string field in settings.Fields)
        {
            string name = SqlFieldName.Extract(field).ToString();
            if (name.Length == 0) continue;

            if (!columns.Types.TryGetValue(name, out string? type))
            {
                throw new InvalidOperationException(
                    $"Table '{settings.FullName}' is adopted, but its column '{name}' does not exist.");
            }

            if (string.Equals(name, settings.PartByRangeFieldName, StringComparison.Ordinal)
                && !IsRangeTypeCompatible(settings.RangeKeyType, type))
            {
                throw new InvalidOperationException(
                    $"Table '{settings.FullName}' partitions by range on '{name}', but the column type is "
                    + $"'{type}', which does not match the configured range key '{settings.RangeKeyType}'.");
            }
        }
    }

    private async Task<(DateTimeOffset? Min, DateTimeOffset? Max)> ReadRangeBounds(
        PgTransaction tx,
        ITableSettings settings,
        string qualifiedBackup,
        CancellationToken cancellationToken)
    {
        string column = SqlTemplate.QuoteIdentifier(settings.PartByRangeFieldName);

        if (settings.RangeKeyType == RangeKeyType.UnixSeconds)
        {
            List<(long?, long?)> rows = await Query(tx,
                $"SELECT min({column}), max({column}) FROM {qualifiedBackup};",
                static reader => (
                    reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0),
                    reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1)),
                [], cancellationToken).ConfigureAwait(false);

            return rows.Count > 0 && rows[0].Item1 is long min && rows[0].Item2 is long max
                ? (DateTimeOffset.FromUnixTimeSeconds(min), DateTimeOffset.FromUnixTimeSeconds(max))
                : (null, null);
        }

        List<(DateTime?, DateTime?)> timestampRows = await Query(tx,
            $"SELECT min({column}), max({column}) FROM {qualifiedBackup};",
            static reader => (
                reader.IsDBNull(0) ? (DateTime?)null : reader.GetDateTime(0),
                reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1)),
            [], cancellationToken).ConfigureAwait(false);

        return timestampRows.Count > 0 && timestampRows[0].Item1 is DateTime minTs && timestampRows[0].Item2 is DateTime maxTs
            ? (ToUtc(minTs), ToUtc(maxTs))
            : (null, null);
    }

    /// <summary>
    /// Interprets the value of a timestamp/date column as UTC. A <c>timestamp without time zone</c>
    /// has no zone of its own, so the library treats it as UTC; a <c>timestamptz</c> already arrives
    /// in UTC from Npgsql, and a local-kind value (if a provider returns one) is converted.
    /// </summary>
    private static DateTimeOffset ToUtc(DateTime value)
    {
        DateTime utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        return new DateTimeOffset(utc);
    }

    /// <summary>
    /// Reads the distinct list-partition key combinations present in the data.
    /// </summary>
    private async Task<List<StrOrNum[]>> ReadListValues(
        PgTransaction tx,
        ITableSettings settings,
        string qualifiedBackup,
        CancellationToken cancellationToken)
    {
        string columns = string.Join(", ", settings.PartByListFieldNames.Select(SqlTemplate.QuoteIdentifier));

        List<StrOrNum[]> rows = await Query(tx,
            $"SELECT DISTINCT {columns} FROM {qualifiedBackup};",
            reader =>
            {
                StrOrNum[] values = new StrOrNum[settings.PartByListFieldNames.Length];

                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = ToPartValue(reader.GetValue(i), settings.FullName, settings.PartByListFieldNames[i]);
                }

                return values;
            },
            [], cancellationToken).ConfigureAwait(false);

        return rows
            .GroupBy(row => string.Join("|", row.Select(value => value.ToFmtString())), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static StrOrNum ToPartValue(object value, string tableName, string columnName)
    {
        return value switch
        {
            string text => text,
            short small => small,
            int integer => integer,
            long big => big,
            decimal number when number == decimal.Truncate(number) && number is >= long.MinValue and <= long.MaxValue
                => (long)number,
            _ => throw new InvalidOperationException(
                $"Table '{tableName}' has a list partition column '{columnName}' of an unsupported type "
                + $"({value.GetType().Name}). Supported are text, varchar, char, smallint, integer, bigint "
                + "and integral numeric."),
        };
    }

    /// <summary>
    /// Reads the column names (in table order) and their formatted types, plus whether the table has
    /// a <c>GENERATED ALWAYS AS IDENTITY</c> column.
    /// </summary>
    private async Task<SourceColumns> ReadColumns(
        PgTransaction? tx,
        string qualifiedTable,
        CancellationToken cancellationToken)
    {
        const string sql = """
SELECT a.attname,
       format_type(a.atttypid, a.atttypmod),
       a.attidentity::text
FROM pg_attribute a
WHERE a.attrelid = to_regclass(@table)
  AND a.attnum > 0
  AND NOT a.attisdropped
ORDER BY a.attnum;
""";

        List<(string Name, string Type, string Identity)> rows = await Query(tx, sql,
            static reader => (reader.GetString(0), reader.GetString(1), reader.GetString(2)),
            [new NpgsqlParameter<string>("table", qualifiedTable)],
            cancellationToken).ConfigureAwait(false);

        return new SourceColumns(
            [.. rows.Select(row => row.Name)],
            rows.ToDictionary(row => row.Name, row => row.Type, StringComparer.Ordinal),
            rows.Any(row => row.Identity == "a"));
    }

    /// <summary>
    /// Rejects a conversion that would leave incoming foreign keys or dependent views attached to
    /// the renamed backup table.
    /// </summary>
    private async Task EnsureNoDependents(
        PgTransaction tx,
        ITableSettings settings,
        string qualifiedTable,
        CancellationToken cancellationToken)
    {
        const string foreignKeysSql = """
SELECT conrelid::regclass::text || ' (' || conname || ')'
FROM pg_constraint
WHERE confrelid = to_regclass(@table)
  AND contype = 'f';
""";

        List<string> foreignKeys = await Query(tx, foreignKeysSql,
            static reader => reader.GetString(0),
            [new NpgsqlParameter<string>("table", qualifiedTable)],
            cancellationToken).ConfigureAwait(false);

        const string viewsSql = """
SELECT DISTINCT v.oid::regclass::text
FROM pg_depend d
JOIN pg_rewrite rw ON rw.oid = d.objid
JOIN pg_class v ON v.oid = rw.ev_class
WHERE d.refobjid = to_regclass(@table)
  AND d.classid = 'pg_rewrite'::regclass
  AND v.relkind IN ('v', 'm');
""";

        List<string> views = await Query(tx, viewsSql,
            static reader => reader.GetString(0),
            [new NpgsqlParameter<string>("table", qualifiedTable)],
            cancellationToken).ConfigureAwait(false);

        if (foreignKeys.Count == 0 && views.Count == 0) return;

        throw new InvalidOperationException(
            $"Table '{settings.FullName}' cannot be adopted because other objects depend on it. "
            + $"Incoming foreign keys: {Join([.. foreignKeys])}. Dependent views: {Join([.. views])}. "
            + "They would keep pointing at the backup table after the rename; drop or recreate them "
            + "around the conversion.");
    }

    private async Task<string> BackupName(
        PgTransaction tx,
        ITableSettings settings,
        CancellationToken cancellationToken)
    {
        string baseName = $"{settings.DatabaseTableName}{settings.SqlPartSeparator}backup$";
        string candidate = TrimIdentifier(baseName);

        for (int i = 1; ; i++)
        {
            string qualified = SqlTemplate.QualifiedName(settings.DatabaseSchemaName, candidate);

            bool exists = await ScalarBool(tx, "SELECT to_regclass(@name) IS NOT NULL;",
                [new NpgsqlParameter<string>("name", qualified)], cancellationToken).ConfigureAwait(false);

            if (!exists) return candidate;

            string suffix = $"_{i.ToString(CultureInfo.InvariantCulture)}";
            candidate = TrimIdentifier(baseName, MaxIdentifierBytes - Encoding.UTF8.GetByteCount(suffix)) + suffix;
        }
    }

    private static string CacheTableName(ITableSettings settings)
        => SqlTemplate.QualifiedName(
            settings.DatabaseSchemaName,
            $"{settings.DatabaseTableName}{settings.SqlPartSeparator}{settings.PartTablePostfix}");

    private static string TrimIdentifier(string value, int maxBytes = MaxIdentifierBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;

        StringBuilder builder = new();
        int bytes = 0;

        foreach (Rune rune in value.EnumerateRunes())
        {
            int runeBytes = rune.Utf8SequenceLength;
            if (bytes + runeBytes > maxBytes) break;

            builder.Append(rune.ToString());
            bytes += runeBytes;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reports whether a PostgreSQL column type can serve as the configured range key.
    /// </summary>
    private static bool IsRangeTypeCompatible(RangeKeyType keyType, string type)
    {
        string normalized = type.ToLowerInvariant();

        return keyType switch
        {
            RangeKeyType.UnixSeconds => normalized is "bigint" or "int8",

            RangeKeyType.TimestampTz =>
                normalized == "timestamptz"
                || (normalized.StartsWith("timestamp", StringComparison.Ordinal)
                    && normalized.Contains("with time zone", StringComparison.Ordinal)),

            RangeKeyType.Timestamp =>
                normalized.StartsWith("timestamp", StringComparison.Ordinal)
                && !normalized.Contains("with time zone", StringComparison.Ordinal),

            RangeKeyType.Date => normalized == "date",

            _ => false,
        };
    }

    private static string Join(string[] values) => values.Length == 0 ? "<none>" : string.Join(", ", values);

    private async Task<int> Execute(
        PgTransaction tx,
        string sql,
        NpgsqlParameter[] parameters,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = tx.Connection.CreateCommand();
        command.Transaction = tx.Transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<T>> Query<T>(
        PgTransaction? tx,
        string sql,
        Func<NpgsqlDataReader, T> read,
        NpgsqlParameter[] parameters,
        CancellationToken cancellationToken)
    {
        if (tx is null)
        {
            return await dataSource.ExecuteReaderList(sql, read, parameters, cancellationToken).ConfigureAwait(false);
        }

        await using NpgsqlCommand command = tx.Connection.CreateCommand();
        command.Transaction = tx.Transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<T> rows = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private async Task<long> ScalarLong(
        PgTransaction tx,
        string sql,
        NpgsqlParameter[] parameters,
        CancellationToken cancellationToken)
    {
        object? value = await Scalar(tx, sql, parameters, cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private async Task<long?> ScalarLongNullable(
        PgTransaction tx,
        string sql,
        NpgsqlParameter[] parameters,
        CancellationToken cancellationToken)
    {
        object? value = await Scalar(tx, sql, parameters, cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private async Task<string?> ScalarString(
        PgTransaction tx,
        string sql,
        NpgsqlParameter[] parameters,
        CancellationToken cancellationToken)
    {
        object? value = await Scalar(tx, sql, parameters, cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : (string)value;
    }

    private async Task<bool> ScalarBool(
        PgTransaction tx,
        string sql,
        NpgsqlParameter[] parameters,
        CancellationToken cancellationToken)
    {
        object? value = await Scalar(tx, sql, parameters, cancellationToken).ConfigureAwait(false);
        return value is not null and not DBNull && (bool)value;
    }

    private async Task<object?> Scalar(
        PgTransaction tx,
        string sql,
        NpgsqlParameter[] parameters,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = tx.Connection.CreateCommand();
        command.Transaction = tx.Transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private readonly record struct TableState(string? RelKind, bool CacheExists)
    {
        public bool Exists => !string.IsNullOrEmpty(RelKind);

        public bool IsPartitioned => RelKind == "p";

        public bool IsPlainTable => RelKind == "r";
    }

    private sealed record SourceColumns(
        string[] Names,
        Dictionary<string, string> Types,
        bool HasAlwaysIdentity);

    [LoggerMessage(
        EventId = 701,
        Level = LogLevel.Information,
        Message = "Table {TableName} is already adopted; skipping conversion.")]
    partial void LogAlreadyAdopted(string tableName);

    [LoggerMessage(
        EventId = 702,
        Level = LogLevel.Information,
        Message = "Converting table {TableName} into a partitioned table; the original is kept as {BackupTable}.")]
    partial void LogConverting(string tableName, string backupTable);

    [LoggerMessage(
        EventId = 703,
        Level = LogLevel.Information,
        Message = "Table {TableName} converted: {Partitions} partition(s) created, rows copied, backup {BackupTable} kept.")]
    partial void LogConverted(string tableName, int partitions, string backupTable);

    [LoggerMessage(
        EventId = 705,
        Level = LogLevel.Information,
        Message = "Table {TableName} converted: {Partitions} partition(s) created, rows copied, original table dropped.")]
    partial void LogConvertedWithoutBackup(string tableName, int partitions);

    [LoggerMessage(
        EventId = 704,
        Level = LogLevel.Information,
        Message = "Table {TableName}: {Rows} row(s) copied into the partitioned root.")]
    partial void LogCopiedRows(string tableName, long rows);
}
