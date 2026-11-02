namespace Sa.Outbox.Redis.Configuration;

/// <summary>
/// Redis key layout of the Outbox. Every key starts with <see cref="Prefix"/>, so several Outboxes
/// can share one Redis database.
/// </summary>
/// <remarks>
/// <para>
/// The provider uses no multi-key Lua scripts, so the keys are ordinary names: they do not need a
/// shared cluster hash tag. Every command touches one key at a time and therefore stays valid on a
/// Redis Cluster.
/// </para>
/// <para>
/// The consumer group itself is not a key: it lives inside the stream, and its name is the
/// consumer group id passed to <c>AddDelivery</c>/<c>AddDeliveryScoped</c>.
/// </para>
/// </remarks>
public sealed class RedisOutboxKeySettings
{
    /// <summary>Contains default values for the key layout.</summary>
    public static class Defaults
    {
        /// <summary>The default key prefix.</summary>
        public const string Prefix = "sa:outbox";
    }

    /// <summary>Gets or sets the key prefix. Default is <c>sa:outbox</c>.</summary>
    public string Prefix { get; set; } = Defaults.Prefix;

    /// <summary>Gets or sets the separator between key segments. Default is <c>:</c>.</summary>
    public string Separator { get; set; } = ":";

    /// <summary>
    /// A key segment cannot carry the separator: <c>part:type</c> would then alias another
    /// (part, type) pair and two unrelated streams would collapse into one key.
    /// </summary>
    private string Segment(string value)
    {
        if (value.Contains(Separator, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The Redis Outbox key segment '{value}' must not contain the separator '{Separator}'.",
                nameof(value));
        }

        return value;
    }

    /// <summary>The message log of one (tenant, part, payload type): an append-only stream.</summary>
    public string StreamKey(int tenantId, string part, string payloadType)
        => $"{Prefix}{Separator}stream{Separator}{tenantId}{Separator}{Segment(part)}{Separator}{Segment(payloadType)}";

    /// <summary>The set of tenant ids ever published — the auto-detect source.</summary>
    public string TenantsKey() => $"{Prefix}{Separator}tenants";
}
