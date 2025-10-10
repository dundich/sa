namespace Sa.Partitional.PostgreSql;

public sealed class PartCacheSettings
{
    public TimeSpan CachedFromDate { get; set; } = TimeSpan.FromDays(1);
}
