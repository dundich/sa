namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Settings that control the partition cache window and how long a loaded snapshot is reused.
/// </summary>
public sealed class PartCacheSettings
{
    /// <summary>
    /// Gets or sets the look-back window applied when reloading partition metadata. A reload keeps
    /// every partition whose range starts at or after <c>now - CachedFromDate</c> and drops those
    /// that ended earlier, so the snapshot runs from that instant onwards.
    /// Default is 1 day, which is enough to still include the partition currently being written to.
    /// </summary>
    public TimeSpan CachedFromDate { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Gets or sets how long a loaded snapshot stays usable before the next read re-queries the
    /// database. The deadline is checked on the read path, so nothing is refreshed in the
    /// background and the cache holds no timer.
    /// Default is 5 minutes.
    /// </summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(5);
}
