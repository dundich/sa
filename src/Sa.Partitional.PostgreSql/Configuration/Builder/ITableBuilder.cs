using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Fluent builder for configuring a single partitioned PostgreSQL table.
/// Chains method calls to declare fields, partitioning strategy, migration behaviour, and tuning knobs.
/// </summary>
public interface ITableBuilder
{
    /// <summary>
    /// Appends raw SQL field definitions to the table (e.g. <c>"created_at timestamptz NOT NULL"</c>).
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
    /// Configures the table for <strong>range partitioning</strong> using a timestamp/timestamptz column.
    /// </summary>
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
    /// <param name="fillFactor">An integer; values are clamped to the 10..100 range.</param>
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
