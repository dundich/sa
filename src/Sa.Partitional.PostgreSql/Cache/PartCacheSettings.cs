namespace Sa.Partitional.PostgreSql;

public class PartCacheSettings
{
    public TimeSpan CachedFromDate { get; set; } = TimeSpan.FromDays(1);
}
