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
    /// Ensures that the specified partitions exist for a given table and date.
    /// This method checks if the specified partitions are present and creates them if they are not.
    /// </summary>
    /// <param name="tableName">The name of the table for which partitions are being ensured.</param>
    /// <param name="date">The date associated with the partition.</param>
    /// <param name="partValues">An array of values that define the partitions (could be strings or numbers).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A value task representing the asynchronous operation, with a boolean result indicating whether the partitions were ensured successfully.</returns>
    ValueTask<bool> EnsureParts(string tableName, DateTimeOffset date, Classes.StrOrNum[] partValues, CancellationToken cancellationToken = default);
}
