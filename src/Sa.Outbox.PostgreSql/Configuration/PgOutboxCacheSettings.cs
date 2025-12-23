namespace Sa.Outbox.PostgreSql;

/// <summary>
/// Represents the settings for caching message types in the Outbox.
/// </summary>
public sealed class PgOutboxCacheSettings
{
    /// <summary>
    /// Gets or sets the duration for which message types are cached.
    /// Default is set to 1 day.
    /// </summary>
    public TimeSpan CacheTypeDuration { get; set; } = TimeSpan.FromDays(1);
}
