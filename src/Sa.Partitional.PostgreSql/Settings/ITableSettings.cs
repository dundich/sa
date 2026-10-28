namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Immutable configuration of a single partitioned PostgreSQL table.
/// Produced by <see cref="ITableBuilder.Build"/> and consumed by migration, repository, and cleanup services.
/// </summary>
public interface ITableSettings
{
    /// <summary>
    /// Gets the fully qualified table name including schema (e.g. <c>"public.events"</c>).
    /// </summary>
    string FullName { get; }

    /// <summary>
    /// Gets the PostgreSQL schema name (e.g. <c>"public"</c> or <c>"outbox"</c>).
    /// </summary>
    string DatabaseSchemaName { get; }

    /// <summary>
    /// Gets the raw table name without schema prefix (e.g. <c>"events"</c>).
    /// </summary>
    string DatabaseTableName { get; }

    /// <summary>
    /// Gets the column name used as the primary-key / row identifier.
    /// </summary>
    string IdFieldName { get; }

    /// <summary>
    /// Gets all column definitions declared for this table (primary key + custom fields).
    /// </summary>
    string[] Fields { get; }

    /// <summary>
    /// Gets the column names used for list partitioning, outermost level first. Empty when the table
    /// has no list levels (a range-only table).
    /// </summary>
    string[] PartByListFieldNames { get; }

    /// <summary>
    /// Gets the column name used for range partitioning. The column is a <c>bigint</c> holding Unix
    /// seconds, never a date/time type: the generated <c>FOR VALUES</c> bounds are integers.
    /// </summary>
    string PartByRangeFieldName { get; }

    /// <summary>
    /// Gets the partitioning strategy — day, month, or year. Range partitioning is always present
    /// (the list levels, when there are any, sit above the range leaf), so this is never <c>null</c>;
    /// without an explicit <c>PartByRange</c> call it defaults to <see cref="PgPartBy.Day"/>.
    /// </summary>
    PgPartBy PartBy { get; }

    /// <summary>
    /// Gets the migration support that supplies list-partition values at runtime.
    /// Null when the table uses range partitioning or has no dynamic migration.
    /// </summary>
    IPartTableMigrationSupport Migration { get; }

    /// <summary>
    /// Gets the separator placed between the table name and each partition key value in a generated
    /// partition name (default: <c>__</c>). For example, root table <c>"events"</c> with the value
    /// <c>"RU"</c> becomes <c>"events__RU"</c>.
    /// </summary>
    string SqlPartSeparator { get; }

    /// <summary>
    /// Gets an optional callback that produces extra SQL to append after the root <c>CREATE TABLE</c> statement.
    /// </summary>
    Func<string>? PostRootSql { get; }

    /// <summary>
    /// Gets an optional callback that produces the <b>name</b> of the generated primary-key constraint.
    /// The <c>PRIMARY KEY</c> clause itself is composed by the library from the id column, the
    /// list-partition columns, and the range column; <c>null</c> means <c>pk_{table}</c>.
    /// </summary>
    Func<string>? ConstraintPkSql { get; }

    /// <summary>
    /// Gets the <c>fillfactor</c> storage parameter of the generated range partitions.
    /// Applied to the date-range children only; the root table and the intermediate list partitions
    /// are emitted without it. When <c>null</c> the clause is omitted and PostgreSQL uses its default (100).
    /// </summary>
    int? FillFactor { get; }

    /// <summary>
    /// Gets the postfix of the cache table that tracks the existing range partitions
    /// (default: <c>part$</c>). The cache table name is <c>{table}{SqlPartSeparator}{PartTablePostfix}</c>,
    /// so root table <c>"events"</c> is tracked in <c>"public"."events__part$"</c>. The postfix is not
    /// part of the partition names themselves.
    /// </summary>
    string PartTablePostfix { get; }

    /// <summary>
    /// Gets a value indicating whether optional <c>DEFAULT</c> partitions are created at every
    /// partitioning level. Each is named <c>{parent}{SqlPartSeparator}def$</c> and is emitted with
    /// <c>PARTITION OF {parent} DEFAULT</c>, so it captures rows whose list value (or, at the deepest
    /// level, whose date) does not match any of the explicitly created partitions.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    bool DefaultPartition { get; }
}
