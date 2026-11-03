namespace Sa.Outbox.Redis.Configuration;

/// <summary>
/// The root settings object of the Redis-backed Outbox: key layout, consumption and retention.
/// Configure it in code through <see cref="IRedisOutboxConfiguration.WithOutboxSettings"/>, from
/// configuration through <see cref="IRedisOutboxConfiguration.WithConfiguration"/>, or through the
/// raw <see cref="IRedisOutboxConfiguration.Options"/> pipeline.
/// </summary>
/// <remarks>
/// The type is the <c>Microsoft.Extensions.Options</c> settings type of the provider: it is
/// registered with <c>AddOptions</c>, bound from configuration and validated when it is first
/// resolved. The nested instances are settable so the configuration binder can replace them; their
/// defaults are the same values the type starts with in code.
/// </remarks>
public sealed class RedisOutboxSettings
{
    /// <summary>Gets or sets the key layout of the Outbox.</summary>
    public RedisOutboxKeySettings KeySettings { get; set; } = new();

    /// <summary>Gets or sets the consumption-side settings.</summary>
    public RedisOutboxConsumeSettings ConsumeSettings { get; set; } = new();

    /// <summary>Gets or sets the automatic stream/dead-letter trimming settings.</summary>
    public RedisOutboxRetentionSettings RetentionSettings { get; set; } = new();

    /// <summary>
    /// Validates the whole settings tree. Called once, when the settings are materialised from the
    /// container — after every <c>WithOutboxSettings</c> action and after configuration binding.
    /// </summary>
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(KeySettings);
        ArgumentNullException.ThrowIfNull(ConsumeSettings);
        ArgumentNullException.ThrowIfNull(RetentionSettings);

        if (string.IsNullOrWhiteSpace(KeySettings.Prefix))
            throw new ArgumentException("KeySettings.Prefix must not be empty.", nameof(KeySettings));

        if (string.IsNullOrEmpty(KeySettings.Separator))
            throw new ArgumentException("KeySettings.Separator must not be empty.", nameof(KeySettings));

        ConsumeSettings.Validate();
        RetentionSettings.Validate();
    }
}
