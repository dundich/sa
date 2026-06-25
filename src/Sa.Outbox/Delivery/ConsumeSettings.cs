namespace Sa.Outbox.Delivery;

/// <summary>
/// Represents the consumption settings for retrieving & processing messages from the Outbox.
/// </summary>
public sealed class ConsumeSettings
{
    /// <summary>
    /// Validates all settings and returns a <see cref="ConsumeSettingsValidationResult"/>.
    /// </summary>
    public ConsumeSettingsValidationResult Validate()
    {
        var errors = new List<string>();

        if (MaxBatchSize <= 0)
            errors.Add($"MaxBatchSize must be greater than 0, got {MaxBatchSize}.");

        if (MaxProcessingIterations < -1)
            errors.Add($"MaxProcessingIterations must be >= -1, got {MaxProcessingIterations}.");

        if (IterationDelay.Ticks < 0)
            errors.Add($"IterationDelay must be >= TimeSpan.Zero, got {IterationDelay}.");

        if (LockDuration <= TimeSpan.Zero)
            errors.Add($"LockDuration must be greater than TimeSpan.Zero, got {LockDuration}.");

        if (LockRenewal >= LockDuration)
            errors.Add($"LockRenewal ({LockRenewal}) must be less than LockDuration ({LockDuration}).");

        if (LockRenewal.Ticks < 0)
            errors.Add($"LockRenewal must be >= TimeSpan.Zero, got {LockRenewal}.");

        if (LookbackInterval.Ticks <= 0)
            errors.Add($"LookbackInterval must be greater than TimeSpan.Zero, got {LookbackInterval}.");

        if (MaxDeliveryAttempts <= 0)
            errors.Add($"MaxDeliveryAttempts must be greater than 0, got {MaxDeliveryAttempts}.");

        if (ConsumeBatchSize.HasValue && ConsumeBatchSize.Value <= 0)
            errors.Add($"ConsumeBatchSize must be greater than 0, got {ConsumeBatchSize}.");

        if (BatchingWindow.Ticks < 0)
            errors.Add($"BatchingWindow must be >= TimeSpan.Zero, got {BatchingWindow}.");

        if (PerTenantTimeout.Ticks < 0)
            errors.Add($"PerTenantTimeout must be >= TimeSpan.Zero, got {PerTenantTimeout}.");

        if (PerTenantMaxDegreeOfParallelism == 0)
            errors.Add($"PerTenantMaxDegreeOfParallelism cannot be 0. Use 1 for sequential or > 1 for parallel.");

        return errors.Count == 0
            ? ConsumeSettingsValidationResult.Valid
            : ConsumeSettingsValidationResult.Fail(errors);
    }

    /// <summary>
    /// Validates all settings, throwing <see cref="InvalidOperationException"/> if invalid.
    /// </summary>
    public void ThrowIfInvalid()
    {
        var result = Validate();
        if (!result.IsValid)
            throw new InvalidOperationException(
                $"Invalid ConsumeSettings: {string.Join("; ", result.Errors)}");
    }

    /// <summary>
    /// Maximum number of processing iterations when greedy mode is enabled.
    /// -1 means unlimited iterations (greedy mode).
    /// </summary>
    /// <remarks>
    /// Consumes data in chunks of size ConsumeBatchSize repeatedly, up to a total of MaxBatchSize.
    /// The MaxProcessingIterations limits the number of these consumption cycles.
    /// When set to -1 (greedy mode), there is no iteration limit — the system will continue consuming batches until it reaches MaxBatchSize or runs out of data.
    /// </remarks>
    public int MaxProcessingIterations { get; set; } = 10;

    /// <summary>
    /// Delay between processing iterations when in greedy mode.
    /// Helps prevent tight-looping when there are no messages.
    /// </summary>
    public TimeSpan IterationDelay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Gets or sets the maximum size of the Outbox message batch for each database poll.
    /// Recommended values: 16, 32, 64, 128, 256, 512, 1024 ...
    /// </summary>
    public int MaxBatchSize { get; set; } = 16;

    /// <summary>
    /// Message lock expiration time.
    /// When a batch of messages for a bus instance is acquired, the messages will be locked (reserved) for that amount of time.
    /// </summary>
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long before <see cref="LockDuration"/> to request a lock renewal.
    /// This should be much shorter than <see cref="LockDuration"/>.
    /// </summary>
    public TimeSpan LockRenewal { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Select outbox messages for processing for the period
    /// </summary>
    public TimeSpan LookbackInterval { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The maximum number of delivery attempts before delivery will not be attempted again.
    /// </summary>
    public int MaxDeliveryAttempts { get; set; } = 3;

    /// <summary>
    /// The maximum number of messages that can take in part
    /// <seealso cref="MaxBatchSize">default value</seealso>
    /// </summary>
    public int? ConsumeBatchSize { get; set; }

    /// <summary>
    /// Time window to accumulate messages before processing a batch.
    /// Delay to wait for "full set of messages" from input messages
    /// </summary>
    public TimeSpan BatchingWindow { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Maximum processing time allowed for each individual tenant.
    /// If processing exceeds this timeout, it will be cancelled and tenant marked as failed.
    /// </summary>
    public TimeSpan PerTenantTimeout { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Maximum number of tenants to process in parallel.
    /// Values:
    /// - 0 or 1: Sequential processing (default)
    /// - > 1: Parallel processing with specified degree
    /// - -1: Use Environment.ProcessorCount
    /// </summary>
    public int PerTenantMaxDegreeOfParallelism { get; set; } = 1;
}
