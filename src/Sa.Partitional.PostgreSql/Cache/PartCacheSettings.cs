namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Settings that control the partition cache window.
/// The cache preloads partition metadata for a configurable period into the future.
/// </summary>
public sealed class PartCacheSettings
{
    /// <summary>
    /// Gets or sets how far ahead (from the current time) the cache should preload partitions.
    /// Default is 1 day.
    /// </summary>
    public TimeSpan CachedFromDate { get; set; } = TimeSpan.FromDays(1);
}
