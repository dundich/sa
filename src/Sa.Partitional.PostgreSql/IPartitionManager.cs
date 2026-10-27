using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSql;

/// <summary>
/// interface for managing partitions in the database
/// </summary>
public interface IPartitionManager
{
    /// <summary>
    /// Migrates the existing partitions in the database.
    /// This method may be used to reorganize or update partitions based on the current state of the data.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation, with an integer result indicating the number of partitions migrated.</returns>
    Task<int> Migrate(CancellationToken cancellationToken = default);

    /// <summary>
    /// Migrates partitions for specific dates.
    /// This method allows for targeted migration of partitions based on the provided date range.
    /// </summary>
    /// <param name="dates">An array of dates for which partitions should be migrated.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation, with an integer result indicating the number of partitions migrated.</returns>
    Task<int> Migrate(DateTimeOffset[] dates, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures that the specified partition exists for a given table and date, creating it when it is
    /// missing from the partition cache.
    /// </summary>
    /// <remarks>
    /// The generated DDL is idempotent (<c>CREATE TABLE IF NOT EXISTS</c> plus the cache upsert), so a
    /// cache miss costs one statement batch and never duplicates a partition. Completion means the
    /// partition exists; everything else is reported as an exception, so there is no result to inspect.
    /// The method used to return <c>Task&lt;bool&gt;</c>, but that bool was <c>false</c> whenever the
    /// partition was merely outside the cache's look-back window — which read as a failure and made
    /// callers either ignore the result or reject a perfectly valid write.
    /// </remarks>
    /// <param name="tableName">The name of the table for which the partition is being ensured.</param>
    /// <param name="date">The date associated with the partition.</param>
    /// <param name="partValues">An array of values that define the partitions (could be strings or numbers).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    Task EnsureParts(
        string tableName,
        DateTimeOffset date,
        StrOrNum[] partValues,
        CancellationToken cancellationToken = default);
}

