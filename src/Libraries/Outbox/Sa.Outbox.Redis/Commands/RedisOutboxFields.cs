namespace Sa.Outbox.Redis.Commands;

/// <summary>
/// Field names of a stream entry. They are deliberately one character where the value is hot
/// (payload, payload id, type, creation time): a Redis stream entry carries its field names in
/// every entry, so a descriptive name costs memory for the whole log.
/// </summary>
internal static class RedisOutboxFields
{
    /// <summary>The serialized payload.</summary>
    public const string Payload = "p";

    /// <summary>The caller-supplied payload id (<c>OutboxMessage.PayloadId</c>).</summary>
    public const string PayloadId = "i";

    /// <summary>The full payload type name, for diagnostics and dead-letter triage.</summary>
    public const string PayloadType = "t";

    /// <summary>Creation instant, Unix milliseconds.</summary>
    public const string CreatedAt = "c";
}
