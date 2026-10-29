using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Fluent builder for configuring a single partitioned PostgreSQL table.
/// Chains method calls to declare fields, partitioning strategy, migration behaviour, and tuning knobs.
/// </summary>
public interface ITableBuilder
{
    /// <summary>
    /// Appends raw SQL field definitions to the table (e.g. <c>"created_at bigint NOT NULL"</c>).
    /// </summary>
    /// <param name="sqlFields">One or more column definitions.</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder AddFields(params string[] sqlFields);

    /// <summary>
    /// Configures the table for <strong>list partitioning</strong> on the specified columns.
    /// All listed fields must share the same type and participate in partition key resolution.
    /// </summary>
    /// <param name="fieldNames">Column names used as partition keys.</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder PartByList(params string[] fieldNames);

    /// <summary>
    /// Configures the table for <strong>range partitioning</strong> on a column holding either Unix
    /// seconds (<c>bigint</c>, the default) or a timestamp/date value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The range key type is detected from the declared column: <c>bigint</c>/<c>int8</c> means Unix
    /// seconds, <c>timestamptz</c> and <c>timestamp [with|without time zone]</c> mean timestamps,
    /// <c>date</c> means dates. A <c>timestamp without time zone</c> is treated as UTC. The cache
    /// table keeps <c>from_date</c>/<c>to_date</c> as Unix seconds in every mode, so retention and
    /// cleanup are unaffected.
    /// </para>
    /// <para>
    /// When the column is not declared among the fields, the library adds
    /// <c>"created_at" bigint NOT NULL</c> to the root table itself and uses the Unix-seconds mode.
    /// Declare the column explicitly to use a timestamp/date key, or call
    /// <see cref="ITableBuilder.WithRangeKey"/> for a domain/alias.
    /// </para>
    /// </remarks>
    /// <param name="partBy">The partitioning granularity — day, month, or year.</param>
    /// <param name="timestampFieldName">
    /// The column to partition on. Used only when <see cref="TimestampAs"/> was not called - that call
    /// is the more specific declaration and always wins, so the result does not depend on the order of
    /// the two calls. When both are omitted, <c>created_at</c> is used.
    /// </param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder PartByRange(PgPartBy partBy, string? timestampFieldName = null);

    /// <summary>
    /// Overrides the auto-detected timestamp field name used for range partitioning.
    /// </summary>
    /// <param name="timestampFieldName">The column name to use as the partition key.</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder TimestampAs(string timestampFieldName);

    /// <summary>
    /// Overrides the type of the range partition column (auto-detected from its declaration by
    /// default).
    /// </summary>
    /// <remarks>
    /// Auto-detection understands <c>bigint</c>/<c>int8</c>, <c>timestamptz</c>,
    /// <c>timestamp [with|without time zone]</c> and <c>date</c>. Use this method for a column
    /// declared through a domain or an alias the detector does not recognise.
    /// </remarks>
    /// <param name="rangeKeyType">The type of the range key.</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder WithRangeKey(RangeKeyType rangeKeyType);

    /// <summary>
    /// Sets the separator placed between the table name and each partition key value in generated
    /// table names (default: <c>__</c>).
    /// </summary>
    /// <remarks>A partition value must not contain this separator: <c>["a__b"]</c> and <c>["a", "b"]</c>
    /// would otherwise produce the same partition table name.</remarks>
    /// <param name="partSeparator">The separator string.</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder WithPartSeparator(string partSeparator);

    /// <summary>
    /// Sets the <c>fillfactor</c> storage parameter of the generated range partitions.
    /// Lower values leave free space for future HOT updates or dynamic partition growth.
    /// </summary>
    /// <remarks>Applied to the date-range child tables only - the root table and the intermediate
    /// list partitions are emitted without it.</remarks>
    /// <param name="fillFactor">An integer between 10 and 100 (PostgreSQL's own range for
    /// <c>fillfactor</c>); anything else throws <see cref="ArgumentOutOfRangeException"/>.</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder WithFillFactor(int fillFactor);

    /// <summary>
    /// Sets the postfix of the cache table that tracks the existing range partitions
    /// (default: <c>part$</c>).
    /// </summary>
    /// <param name="postfix">The suffix string; must not be <c>null</c> or whitespace.</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder WithPartTablePostfix(string postfix);

    /// <summary>
    /// Enables an optional <c>DEFAULT</c> partition at every partitioning level, so rows that do not
    /// match any explicitly created partition are kept instead of rejected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A catch-all is created under the root and under each list prefix (for example
    /// <c>{table}{partSeparator}def$</c>, <c>{table}{partSeparator}FR{partSeparator}def$</c>),
    /// plus one under the deepest list prefix for dates outside the created ranges. Each is a plain
    /// leaf: it is not itself further partitioned.
    /// </para>
    /// <para>
    /// PostgreSQL refuses to create a partition while an existing <c>DEFAULT</c> holds rows that would
    /// belong to it (<c>updated partition constraint for default partition would be violated by some
    /// row</c>). When this option is on, the migration moves those rows out of the default into a
    /// transaction-scoped staging table, creates the partition, and moves them back — so a catch-all
    /// that already holds data does not block later migrations.
    /// </para>
    /// <para>
    /// A list value literally named <c>def$</c> would collide with the generated name.
    /// </para>
    /// </remarks>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder WithDefaultPartition();

    /// <summary>
    /// Registers a callback that produces extra SQL to run after the root-table <c>CREATE TABLE</c> statement.
    /// </summary>
    /// <param name="postSql">A factory producing the SQL fragment.</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder AddPostSql(Func<string> postSql);

    /// <summary>
    /// Overrides the <b>name</b> of the generated primary-key constraint (default: <c>pk_{table}</c>).
    /// </summary>
    /// <remarks>The library emits <c>CONSTRAINT "{name}" PRIMARY KEY ({columns})</c> itself, so the
    /// callback returns the name only - a full <c>CONSTRAINT ... PRIMARY KEY (...)</c> clause here would
    /// be quoted as a single identifier and the DDL would not parse. The columns of the constraint
    /// follow from the declaration: the id column, the list-partition columns, and the range column.</remarks>
    /// <param name="pkSql">A factory producing the constraint name.</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder AddConstraintPkSql(Func<string> pkSql);

    /// <summary>
    /// Finalises the builder and returns an immutable <see cref="ITableSettings"/> snapshot.
    /// </summary>
    /// <returns>The validated table settings.</returns>
    ITableSettings Build();

    /// <summary>
    /// Sets what happens to the original plain table after an adopted table is converted
    /// (default: <see cref="ConversionBackupMode.Keep"/>).
    /// </summary>
    /// <remarks>
    /// <see cref="ConversionBackupMode.Drop"/> removes the backup inside the conversion transaction,
    /// after the copied row count has been verified. The transaction is still atomic, but a committed
    /// conversion cannot be rolled back afterwards — keep the default when the data needs a recovery
    /// window. Has no effect on tables created by the library.
    /// </remarks>
    /// <param name="mode">Whether to keep or drop the original table.</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder WithConversionBackup(ConversionBackupMode mode);

    /// <summary>
    /// Attaches a custom migration provider that supplies list-partition values at runtime.
    /// </summary>
    /// <param name="migrationSupport">The migration support implementation.</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder AddMigration(IPartTableMigrationSupport migrationSupport);

    /// <summary>
    /// Attaches a lazy migration callback that resolves partition values asynchronously.
    /// </summary>
    /// <param name="getPartValues">A function that returns partition values when triggered.</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder AddMigration(Func<CancellationToken, Task<StrOrNum[][]>> getPartValues);

    /// <summary>
    /// Declares static list-partition values to create eagerly at startup.
    /// </summary>
    /// <param name="partValues">One or more partition values (strings or numbers) forming a single
    /// nesting level. Pass one argument per <c>PartByList</c> column, or none for a range-only table.</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder AddMigration(params StrOrNum[] partValues);

    /// <summary>
    /// Declares a parent-child hierarchy of list-partition values.
    /// </summary>
    /// <remarks><c>AddMigration("a", "b")</c> binds to the <c>params</c> overload above and declares two
    /// independent values, not a parent-child pair. Nesting has to be spelled out with an explicit
    /// array: <c>AddMigration("a", ["b"])</c>.</remarks>
    /// <param name="parent">The parent partition value.</param>
    /// <param name="childs">Child partition values nested under the parent.</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder AddMigration(StrOrNum parent, StrOrNum[] childs)
    {
        foreach (StrOrNum child in childs) AddMigration(parent, child);
        return this;
    }
}
