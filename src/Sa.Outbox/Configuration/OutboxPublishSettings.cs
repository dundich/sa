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


/// <summary>
/// Extension methods for fluent configuration of <see cref="OutboxPublishSettings"/>.
/// </summary>
public static class OutboxPublishSettingsExtensions
{
    /// <summary>
    /// Sets the maximum batch size for publishing messages.
    /// </summary>
    /// <param name="settings">The publish settings.</param>
    /// <param name="batchSize">The batch size (recommended to use optimized values).</param>
    /// <returns>The configured settings instance.</returns>
    public static OutboxPublishSettings WithMaxBatchSize(
        this OutboxPublishSettings settings,
        int batchSize)
    {
        if (batchSize <= 0)
            throw new ArgumentException("Batch size must be positive", nameof(batchSize));

        settings.MaxBatchSize = batchSize;
        return settings;
    }


    /// <summary>
    /// Sets the batch size to 16 (smallest optimized size).
    /// </summary>
    public static OutboxPublishSettings WithSmallBatch(this OutboxPublishSettings settings)
        => settings.WithMaxBatchSize(16);

    /// <summary>
    /// Sets the batch size to 32.
    /// </summary>
    public static OutboxPublishSettings WithMediumSmallBatch(this OutboxPublishSettings settings)
        => settings.WithMaxBatchSize(32);

    /// <summary>
    /// Sets the batch size to 64 (default).
    /// </summary>
    public static OutboxPublishSettings WithMediumBatch(this OutboxPublishSettings settings)
        => settings.WithMaxBatchSize(64);

    /// <summary>
    /// Sets the batch size to 128.
    /// </summary>
    public static OutboxPublishSettings WithNormalBatch(this OutboxPublishSettings settings)
        => settings.WithMaxBatchSize(128);

    /// <summary>
    /// Sets the batch size to 256.
    /// </summary>
    public static OutboxPublishSettings WithLargeBatch(this OutboxPublishSettings settings)
        => settings.WithMaxBatchSize(256);

    /// <summary>
    /// Sets the batch size to 512.
    /// </summary>
    public static OutboxPublishSettings WithExtraLargeBatch(this OutboxPublishSettings settings)
        => settings.WithMaxBatchSize(512);

    /// <summary>
    /// Sets the batch size to 1024.
    /// </summary>
    public static OutboxPublishSettings WithHugeBatch(this OutboxPublishSettings settings)
        => settings.WithMaxBatchSize(1024);

    /// <summary>
    /// Sets the batch size to 2048.
    /// </summary>
    public static OutboxPublishSettings WithMassiveBatch(this OutboxPublishSettings settings)
        => settings.WithMaxBatchSize(2048);

    /// <summary>
    /// Sets the batch size to 4096 (largest optimized size).
    /// </summary>
    public static OutboxPublishSettings WithMaximumBatch(this OutboxPublishSettings settings)
        => settings.WithMaxBatchSize(4096);

}
