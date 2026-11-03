namespace Sa.Partitional.PostgreSql;

/// <summary>
/// What happens to the original table after an adopted table has been converted into a partitioned
/// one.
/// </summary>
public enum ConversionBackupMode
{
    /// <summary>
    /// Keep the original table under <c>{table}{separator}backup$</c>. This is the default: the
    /// conversion can be verified — and, if necessary, the data recovered — before the backup is
    /// dropped manually.
    /// </summary>
    Keep = 0,

    /// <summary>
    /// Drop the original table inside the conversion transaction, after the copied row count has
    /// been verified. The conversion itself is still atomic, but there is no backup left to fall
    /// back on once the transaction commits.
    /// </summary>
    Drop = 1,
}
