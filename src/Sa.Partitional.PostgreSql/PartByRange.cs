namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Enumerates the possible partitional ranges for a PostgreSQL database.
/// </summary>
public enum PartByRange
{
    /// <summary>
    /// Partition by day.
    /// </summary>
    Day,
    /// <summary>
    /// Partition by month.
    /// </summary>
    Month,
    /// <summary>
    /// Partition by year.
    /// </summary>
    Year
}