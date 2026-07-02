using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Represents information about a range-partitioned child table in PostgreSQL.
/// </summary>
/// <param name="Id">Fully qualified partition identifier, including schema (e.g. <c>"public.outbox__20260626"</c>).</param>
/// <param name="RootTableName">The name of the parent/root table from which this partition is derived.</param>
/// <param name="PartValues">Partition key values — may be strings (for list) or numbers (for range dates).</param>
/// <param name="PartBy">The <see cref="PgPartBy"/> strategy that governs how this partition was created.</param>
/// <param name="FromDate">The effective start date of the partition.</param>
public sealed record PartByRangeInfo(
    string Id,
    string RootTableName,
    StrOrNum[] PartValues,
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
    /// <param name="date">The reference date used to compute the partition boundary.</param>
    /// <param name="partValues">Partition key values.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The number of rows affected (typically 1 on successful CREATE).</returns>
    Task<int> CreatePart(
        string tableName,
        DateTimeOffset date,
        StrOrNum[] partValues,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures that all tables have partitions covering each date in <paramref name="dates"/>.
    /// Missing partitions are created automatically.
    /// </summary>
    /// <param name="dates">Array of dates to migrate.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>Total number of newly created partitions across all tables.</returns>
    Task<int> Migrate(DateTimeOffset[] dates, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures partitions for all tables, resolving list-partition values dynamically via <paramref name="resolve"/>.
    /// </summary>
    /// <param name="dates">Dates to ensure partitions for.</param>
    /// <param name="resolve">A function that receives a table name and returns the expected partition values.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>Total number of newly created partitions.</returns>
    Task<int> Migrate(
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
    Task<int> DropPartsToDate(string tableName, DateTimeOffset toDate, CancellationToken cancellationToken = default);
}
