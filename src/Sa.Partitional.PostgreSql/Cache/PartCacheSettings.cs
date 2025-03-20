namespace Sa.Partitional.PostgreSql;

public class PartCacheSettings
{
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan CachedFromDate { get; set; } = TimeSpan.FromDays(1);
}
