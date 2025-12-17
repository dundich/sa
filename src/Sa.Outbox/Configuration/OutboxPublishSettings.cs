namespace Sa.Outbox;

/// <summary>
/// Settings for publishing messages from the Outbox.
/// </summary>
public sealed class OutboxPublishSettings
{
    /// <summary>
    /// The maximum batch size of messages to be sent at once.
    /// Default value: 16.
    /// for array pool size: 16, 32, 64, 128, 256, 512, 1024, 2048, 4096
    /// </summary>
    public int MaxBatchSize { get; set; } = 64;
}
