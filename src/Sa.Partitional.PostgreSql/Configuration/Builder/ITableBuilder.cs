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
    /// The column to partition on. When <c>null</c>, the first <c>timestamptz</c>-typed column is used automatically.
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
    /// Sets the separator character used between schema and table names in generated SQL (default: <c>_</c>).
    /// </summary>
    /// <param name="partSeparator">The separator character(s).</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder WithPartSeparator(string partSeparator);

    /// <summary>
    /// Sets the <c>fillfactor</c> storage parameter for both root and child tables.
    /// Lower values leave free space for future HOT updates or dynamic partition growth.
    /// </summary>
    /// <param name="fillFactor">An integer between 1 and 100.</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder WithFillFactor(int fillFactor);

    /// <summary>
    /// Sets the postfix appended to child/partition table names (default: <c>__part</c>).
    /// </summary>
    /// <param name="postfix">The suffix string.</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder WithPartTablePostfix(string postfix);

    /// <summary>
    /// Registers a callback that produces extra SQL to run after the root-table <c>CREATE TABLE</c> statement.
    /// </summary>
    /// <param name="postSql">A factory producing the SQL fragment.</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder AddPostSql(Func<string> postSql);

    /// <summary>
    /// Registers a callback that produces custom <c>CHECK</c> / <c>PRIMARY KEY</c> constraint SQL.
    /// </summary>
    /// <param name="pkSql">A factory producing the constraint definition.</param>
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
    /// <param name="partValues">One or more partition values (strings or numbers).</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder AddMigration(params StrOrNum[] partValues);

    /// <summary>
    /// Declares a parent-child hierarchy of list-partition values.
    /// </summary>
    /// <param name="parent">The parent partition value.</param>
    /// <param name="childs">Child partition values nested under the parent.</param>
    /// <returns>The same <see cref="ITableBuilder"/> for chaining.</returns>
    ITableBuilder AddMigration(StrOrNum parent, StrOrNum[] childs)
    {
        foreach (StrOrNum child in childs) AddMigration(parent, child);
        return this;
    }
}
