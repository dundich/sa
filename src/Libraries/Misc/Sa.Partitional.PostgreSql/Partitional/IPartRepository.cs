namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Represents information about a range-partitioned child table in PostgreSQL.
/// </summary>
/// <param name="Id">Fully qualified partition identifier, including schema (e.g. <c>"public"."outbox__y2026m06d26"</c>).</param>
/// <param name="RootTableName">The name of the parent/root table from which this partition is derived.</param>
/// <param name="PartValues">Partition key values — may be strings (for list) or numbers (for range dates). Read-only: this is metadata read back from the cache.</param>
/// <param name="PartBy">The <see cref="PgPartBy"/> strategy that governs how this partition was created.</param>
/// <param name="FromDate">The effective start date of the partition.</param>
public sealed record PartByRangeInfo(
    string Id,
    string RootTableName,
    IReadOnlyList<StrOrNum> PartValues,
    PgPartBy PartBy,
    DateTimeOffset FromDate);

/// <summary>
/// Repository that executes DDL statements for creating, querying, and dropping PostgreSQL partitions.
/// </summary>
public interface IPartRepository
{
    /// <summary>
    /// Creates a single partition (child table) for the given table, date, and partition values.
    /// For range partitioning this creates a date-bounded child; for list partitioning it creates a value-constrained child.
    /// </summary>
    /// <param name="tableName">The root table name (schema-qualified or unqualified).</param>
    /// <param name="periodStart">The reference date used to compute the partition boundary.</param>
    /// <param name="partValues">Partition key values.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// The result of the underlying multi-statement command: it is <c>0</c> for pure DDL on most
    /// servers (PostgreSQL reports no row count for <c>CREATE TABLE</c>), not <c>1</c>.
    /// </returns>
    Task<int> CreatePartAsync(
        string tableName,
        DateTimeOffset periodStart,
        StrOrNum[] partValues,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures that all tables have partitions covering each date in <paramref name="dates"/>.
    /// Missing partitions are created automatically.
    /// </summary>
    /// <param name="dates">Array of dates to migrate.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// Number of DDL statements executed, not the number of partitions actually created: the
    /// generated DDL is <c>IF NOT EXISTS</c>, so a re-run executes the same statements and counts
    /// them again.
    /// </returns>
    Task<int> MigrateAsync(DateTimeOffset[] dates, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures partitions for all tables, resolving list-partition values dynamically via <paramref name="resolve"/>.
    /// </summary>
    /// <param name="dates">Dates to ensure partitions for.</param>
    /// <param name="resolve">A function that receives a table name and returns the expected partition values.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>Number of DDL statements executed, not the number of partitions created (see the other overload).</returns>
    Task<int> MigrateAsync(
        DateTimeOffset[] dates,
        Func<string, Task<StrOrNum[][]>> resolve,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all range partitions for a table starting from <paramref name="fromDate"/>.
    /// </summary>
    /// <param name="tableName">The root table name.</param>
    /// <param name="fromDate">The lower-bound date (inclusive).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A list of <see cref="PartByRangeInfo"/> describing each partition.</returns>
    Task<List<PartByRangeInfo>> GetPartsFromDate(
        string tableName,
        DateTimeOffset fromDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all range partitions for a table up to and including <paramref name="toDate"/>.
    /// </summary>
    /// <param name="tableName">The root table name.</param>
    /// <param name="toDate">The upper-bound date (inclusive).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A list of <see cref="PartByRangeInfo"/> describing each partition.</returns>
    Task<List<PartByRangeInfo>> GetPartsToDate(
        string tableName,
        DateTimeOffset toDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops all partitions whose <see cref="PartByRangeInfo.FromDate"/> is less than or equal to <paramref name="toDate"/>.
    /// This is the core primitive used by <see cref="IPartCleanupService"/>.
    /// </summary>
    /// <param name="tableName">The root table name.</param>
    /// <param name="toDate">Upper-bound date — partitions up to and including this date will be dropped.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The number of partitions dropped.</returns>
    Task<int> DropPartsToDateAsync(string tableName, DateTimeOffset toDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Archives all partitions whose <see cref="PartByRangeInfo.FromDate"/> is less than or equal to
    /// <paramref name="toDate"/>: each one is detached from its parent and moved — under the same name —
    /// into <paramref name="archiveSchemaName"/>, and its metadata row is marked as archived instead of
    /// being deleted. Already archived rows are not returned by the underlying query, so a repeated run
    /// is a no-op. Equivalent to <see cref="ArchiveCollisionMode.Fail"/> for a name clash.
    /// </summary>
    /// <remarks>
    /// Requires PostgreSQL 13+ (<c>DETACH PARTITION CONCURRENTLY</c> is 12+, but moving a partition to
    /// another schema is 13+). The archive schema is created when missing. The archived table keeps its
    /// name, so all modules sharing an archive schema need distinct table names.
    /// </remarks>
    /// <param name="tableName">The root table name.</param>
    /// <param name="toDate">Upper-bound date — partitions up to and including this date will be archived.</param>
    /// <param name="archiveSchemaName">The schema the detached partitions are moved into.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The number of partitions archived.</returns>
    Task<int> ArchivePartsToDateAsync(
        string tableName,
        DateTimeOffset toDate,
        string archiveSchemaName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as the other overload, but chooses what happens when the archive schema already holds a
    /// table with the partition's name (<see cref="ArchiveCollisionMode"/>).
    /// </summary>
    /// <param name="tableName">The root table name.</param>
    /// <param name="toDate">Upper-bound date — partitions up to and including this date will be archived.</param>
    /// <param name="archiveSchemaName">The schema the detached partitions are moved into.</param>
    /// <param name="collisionMode">What to do when the target name is already taken.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The number of partitions archived.</returns>
    Task<int> ArchivePartsToDateAsync(
        string tableName,
        DateTimeOffset toDate,
        string archiveSchemaName,
        ArchiveCollisionMode collisionMode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops list-level partitions of <paramref name="tableName"/> whose whole subtree is empty.
    /// </summary>
    /// <remarks>
    /// Cleanup only retires range leaves; without this, a list value that stopped being used keeps its
    /// intermediate partition and its <c>DEFAULT</c> child forever. A partition is dropped only when
    /// nothing in its subtree holds a row and its value path is not a prefix of a value the table's
    /// migration support reports (a declared branch is never pruned, so migration does not fight the
    /// cleanup). Opt-in via <see cref="PartCleanupScheduleSettings.PruneEmptyListParts"/>.
    /// </remarks>
    /// <param name="tableName">The root table name.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The number of list-level partitions dropped.</returns>
    Task<int> PruneEmptyListPartsAsync(string tableName, CancellationToken cancellationToken = default);
}
