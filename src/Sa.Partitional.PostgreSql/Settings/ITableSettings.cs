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
    /// Gets the column names used for list partitioning. Empty when the table uses range partitioning.
    /// </summary>
    string[] PartByListFieldNames { get; }

    /// <summary>
    /// Gets the column name used for range partitioning (typically a <c>timestamptz</c> column).
    /// Empty when the table uses list partitioning.
    /// </summary>
    string PartByRangeFieldName { get; }

    /// <summary>
    /// Gets the partitioning strategy — day, month, or year for range; <c>null</c> for list partitioning.
    /// </summary>
    PgPartBy PartBy { get; }

    /// <summary>
    /// Gets the migration support that supplies list-partition values at runtime.
    /// Null when the table uses range partitioning or has no dynamic migration.
    /// </summary>
    IPartTableMigrationSupport Migration { get; }

    /// <summary>
    /// Gets the separator between schema and table name in generated partition identifiers (default: <c>_</c>).
    /// </summary>
    string SqlPartSeparator { get; }

    /// <summary>
    /// Gets an optional callback that produces extra SQL to append after the root <c>CREATE TABLE</c> statement.
    /// </summary>
    Func<string>? PostRootSql { get; }

    /// <summary>
    /// Gets an optional callback that produces custom constraint SQL (e.g. additional <c>CHECK</c> clauses).
    /// </summary>
    Func<string>? ConstraintPkSql { get; }

    /// <summary>
    /// Gets the <c>fillfactor</c> storage parameter for <c>CREATE TABLE</c> / <c>ALTER TABLE</c> commands.
    /// When <c>null</c>, PostgreSQL uses its default (100).
    /// </summary>
    int? FillFactor { get; }

    /// <summary>
    /// Gets the suffix appended to child/partition table names (default: <c>__part</c>).
    /// For example, root table <c>"events"</c> with date 2026-06-26 becomes <c>"events__part__y2026m06d26"</c>.
    /// </summary>
    string PartTablePostfix { get; }
}
