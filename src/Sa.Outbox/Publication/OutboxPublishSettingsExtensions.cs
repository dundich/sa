namespace Sa.Outbox.Publication;

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
    public static OutboxPublishSettings WithBatch16(this OutboxPublishSettings settings)
        => settings.WithMaxBatchSize(16);

    /// <summary>
    /// Sets the batch size to 32.
    /// </summary>
    public static OutboxPublishSettings WithBatch32(this OutboxPublishSettings settings)
        => settings.WithMaxBatchSize(32);

    /// <summary>
    /// Sets the batch size to 64 (default).
    /// </summary>
    public static OutboxPublishSettings WithBatch64(this OutboxPublishSettings settings)
        => settings.WithMaxBatchSize(64);

    /// <summary>
    /// Sets the batch size to 128.
    /// </summary>
    public static OutboxPublishSettings WithBatch128(this OutboxPublishSettings settings)
        => settings.WithMaxBatchSize(128);

    /// <summary>
    /// Sets the batch size to 256.
    /// </summary>
    public static OutboxPublishSettings WithBatch256(this OutboxPublishSettings settings)
        => settings.WithMaxBatchSize(256);

    /// <summary>
    /// Sets the batch size to 512.
    /// </summary>
    public static OutboxPublishSettings WithBatch512(this OutboxPublishSettings settings)
        => settings.WithMaxBatchSize(512);

    /// <summary>
    /// Sets the batch size to 1024.
    /// </summary>
    public static OutboxPublishSettings WithBatch1024(this OutboxPublishSettings settings)
        => settings.WithMaxBatchSize(1024);

    /// <summary>
    /// Sets the batch size to 2048.
    /// </summary>
    public static OutboxPublishSettings WithBatch2048(this OutboxPublishSettings settings)
        => settings.WithMaxBatchSize(2048);

    /// <summary>
    /// Sets the batch size to 4096 (largest optimized size).
    /// </summary>
    public static OutboxPublishSettings WithMaximumBatch(this OutboxPublishSettings settings)
        => settings.WithMaxBatchSize(4096);

}
