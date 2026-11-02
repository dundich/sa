namespace Sa.Outbox.Redis.Configuration;

/// <summary>
/// Automatic trimming of the message streams the Outbox writes.
/// </summary>
/// <remarks>
/// <para>
/// Trimming is <b>off by default</b>: a Redis stream is also its consumer groups' history, and a
/// plain <c>MAXLEN</c> would delete entries that are still pending or not yet delivered. When
/// enabled, the provider never trims by raw length — it computes a safe floor from the consumer
/// groups (<c>XINFO GROUPS</c> / <c>XPENDING</c>) and removes only entries below both that floor and
/// the age limit.
/// </para>
/// <para>
/// There is no dead-letter stream to bound: poisoned messages go to
/// <see cref="Sa.Outbox.Redis.Poison.IRedisOutboxPoisonHandler"/>, and the provider acknowledges them.
/// </para>
/// </remarks>
public sealed class RedisOutboxRetentionSettings
{
    /// <summary>Contains default values for the retention settings.</summary>
    public static class Defaults
    {
        /// <summary>The default maximum age of an entry in a message stream (7 days).</summary>
        public static TimeSpan StreamMaxAge => TimeSpan.FromDays(7);

        /// <summary>The default minimum interval between trims of one stream (5 minutes).</summary>
        public static TimeSpan TrimInterval => TimeSpan.FromMinutes(5);

        /// <summary>The default maximum number of entries one trim command may remove (10 000).</summary>
        public const int TrimLimit = 10_000;
    }

    /// <summary>
    /// Gets or sets whether automatic trimming is enabled. Default is <see langword="false"/>, which
    /// keeps the pre-retention behaviour: streams are never trimmed by the provider.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum age of an entry in a message stream. <see cref="TimeSpan.Zero"/>
    /// disables the age limit. Default is 7 days.
    /// </summary>
    /// <remarks>
    /// The age is only an upper bound: an entry older than this is still kept while any consumer
    /// group needs it (it is pending) or has not reached it yet.
    /// </remarks>
    public TimeSpan StreamMaxAge { get; set; } = Defaults.StreamMaxAge;

    /// <summary>
    /// Gets or sets how often one stream may be trimmed at most. Trimming runs inline on publish,
    /// so this is the throttle that keeps a busy stream from paying the <c>XINFO</c>/<c>XPENDING</c>
    /// round trips on every batch. Default is 5 minutes.
    /// </summary>
    public TimeSpan TrimInterval { get; set; } = Defaults.TrimInterval;

    /// <summary>
    /// Gets or sets whether <c>XTRIM</c> uses the approximate (<c>~</c>) mode. Approximate trimming
    /// works on whole macro nodes: it is much cheaper for Redis and may leave slightly more entries
    /// than the limit. Default is <see langword="true"/>.
    /// </summary>
    public bool Approximate { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of entries one trim command may remove, passed to
    /// <c>XTRIM ... LIMIT</c>. <c>0</c> removes the limit. Default is 10 000.
    /// </summary>
    /// <remarks>
    /// Redis only accepts <c>LIMIT</c> together with the approximate (<c>~</c>) mode, so a non-zero
    /// value requires <see cref="Approximate"/> to be enabled.
    /// </remarks>
    public int TrimLimit { get; set; } = Defaults.TrimLimit;

    internal void Validate()
    {
        if (StreamMaxAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(StreamMaxAge), StreamMaxAge, "StreamMaxAge must not be negative.");

        if (TrimInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(TrimInterval), TrimInterval, "TrimInterval must be > TimeSpan.Zero.");

        if (TrimLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(TrimLimit), TrimLimit, "TrimLimit must not be negative.");

        if (!Approximate && TrimLimit > 0)
        {
            throw new ArgumentException(
                "TrimLimit requires Approximate = true: Redis rejects LIMIT without the approximate '~' mode.",
                nameof(TrimLimit));
        }
    }
}
