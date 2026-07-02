namespace Sa.Outbox.Publication;

/// <summary>
/// Immutable settings for publishing messages from the Outbox.
/// </summary>
public sealed record OutboxPublishSettings(int MaxBatchSize = 64)
{
    /// <summary>
    /// Creates a copy with a new <see cref="MaxBatchSize"/>.
    /// </summary>
    public OutboxPublishSettings WithMaxBatchSize(int batchSize)
        => this with { MaxBatchSize = batchSize };
}
