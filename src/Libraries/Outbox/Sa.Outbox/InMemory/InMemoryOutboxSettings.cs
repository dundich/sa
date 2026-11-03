namespace Sa.Outbox.InMemory;

/// <summary>
/// Settings of the in-memory outbox provider.
/// </summary>
/// <remarks>
/// Deliberately tiny: the provider has no connection, no schema and no retention to configure.
/// Everything the delivery pipeline needs comes from the core's consumer settings.
/// </remarks>
public sealed class InMemoryOutboxSettings
{
    /// <summary>
    /// Whether the provider is registered at all. A disabled provider registers nothing: no store,
    /// no contracts, no schedule.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The maximum number of messages held at once; <c>0</c> (the default) means unlimited.
    /// </summary>
    /// <remarks>
    /// A positive limit makes an accidental unbounded growth fail loudly instead of eating memory in
    /// a long-running development session: a publish that would exceed the limit throws
    /// <see cref="Sa.Outbox.Exceptions.OutboxException"/> and stores nothing from that batch.
    /// </remarks>
    public int Capacity { get; set; }

    internal void Validate()
    {
        if (Capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(Capacity), Capacity, "The capacity must not be negative.");
    }
}
