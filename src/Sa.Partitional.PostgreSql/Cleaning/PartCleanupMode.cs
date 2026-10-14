namespace Sa.Partitional.PostgreSql;

/// <summary>
/// How the cleanup job retires partitions older than <see cref="PartCleanupScheduleSettings.DropPartsAfterRetention"/>.
/// </summary>
/// <remarks>
/// Deliberately a plain <c>enum</c> rather than an <c>Enumeration&lt;T&gt;</c> record (like
/// <see cref="PgPartBy"/>): the value is a <i>setting</i>, so it has to bind from the configuration
/// section by name, which the standard binder does for an <c>enum</c> and cannot do for a record with
/// a two-argument constructor. Same reason <see cref="PartByRange"/> is a plain enum.
/// </remarks>
public enum PartCleanupMode
{
    /// <summary>
    /// Drop the expired partitions (the historical behaviour): <c>DROP TABLE IF EXISTS</c> plus
    /// removal of the metadata row from the partition cache table.
    /// </summary>
    Drop = 0,

    /// <summary>
    /// Detach the expired partitions and move them — under the same name — into the archive schema
    /// (<see cref="PartCleanupScheduleSettings.ArchiveSchemaName"/>), keeping the data and marking the
    /// metadata row as archived instead of deleting it.
    /// </summary>
    Archive = 1,
}
