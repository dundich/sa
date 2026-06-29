namespace Sa.Outbox.Delivery;

/// <summary>
/// Represents the configuration for a message delivery consumer group.
/// Provides fluent extension methods for bootstrap configuration at startup.
/// At runtime, settings are managed via <see cref="IOutboxSettingsManager"/> which works with immutable
/// <see cref="OutboxConsumerSettings"/> snapshots.
/// </summary>
public sealed class ConsumerGroupSettings(string consumerGroupId, bool isSingleton)
{
    /// <summary>
    /// Group identity for consuming. Sanitized to lowercase with underscores.
    /// </summary>
    public string ConsumerGroupId => consumerGroupId;

    /// <summary>
    /// Whether the associated consumer uses singleton lifetime in DI.
    /// </summary>
    public bool AsSingleton => isSingleton;

    /// <summary>
    /// Gets the scheduling settings for the delivery job.
    /// </summary>
    public ScheduleSettings ScheduleSettings { get; } = new();

    /// <summary>
    /// Gets the consumption settings for processing messages.
    /// </summary>
    public ConsumeSettings ConsumeSettings { get; } = new();

    // ── Conversion to immutable snapshot ──────────────────────

    /// <summary>
    /// Converts this mutable bootstrap settings into an immutable <see cref="OutboxConsumerSettings"/>
    /// suitable for runtime management via <see cref="IOutboxSettingsManager"/>.
    /// </summary>
    internal OutboxConsumerSettings ToCanonical()
    {
        return new OutboxConsumerSettingsBuilder()
            .WithConsumerGroupId(ConsumerGroupId)
            .AsSingleton(AsSingleton)
            .WithInterval(ScheduleSettings.Interval)
            .WithInitialDelay(ScheduleSettings.InitialDelay)
            .WithConcurrencyLimit(ScheduleSettings.ConcurrencyLimit)
            .WithMaxConcurrency(ScheduleSettings.MaxConcurrency)
            .WithRetryCountOnError(ScheduleSettings.RetryCountOnError)
            .WithMaxBatchSize(ConsumeSettings.MaxBatchSize)
            .WithMaxProcessingIterations(ConsumeSettings.MaxProcessingIterations)
            .WithIterationDelay(ConsumeSettings.IterationDelay)
            .WithLockDuration(ConsumeSettings.LockDuration)
            .WithLockRenewal(ConsumeSettings.LockRenewal)
            .WithLookbackInterval(ConsumeSettings.LookbackInterval)
            .WithMaxDeliveryAttempts(ConsumeSettings.MaxDeliveryAttempts)
            .WithBatchingWindow(ConsumeSettings.BatchingWindow)
            .WithPerTenantTimeout(ConsumeSettings.PerTenantTimeout)
            .WithPerTenantMaxDegreeOfParallelism(ConsumeSettings.PerTenantMaxDegreeOfParallelism)
            .Build();
    }

    /// <summary>
    /// Applies an immutable <see cref="OutboxConsumerSettings"/> snapshot back onto this mutable settings.
    /// Called during startup bootstrap and on runtime updates from <see cref="IOutboxSettingsManager"/>.
    /// </summary>
    internal void FromCanonical(OutboxConsumerSettings canonical)
    {
        ScheduleSettings.Interval = canonical.Interval;
        ScheduleSettings.InitialDelay = canonical.InitialDelay;
        ScheduleSettings.ConcurrencyLimit = canonical.ConcurrencyLimit;
        ScheduleSettings.MaxConcurrency = canonical.MaxConcurrency;
        ScheduleSettings.RetryCountOnError = canonical.RetryCountOnError;

        ConsumeSettings.MaxBatchSize = canonical.MaxBatchSize;
        ConsumeSettings.MaxProcessingIterations = canonical.MaxProcessingIterations;
        ConsumeSettings.IterationDelay = canonical.IterationDelay;
        ConsumeSettings.LockDuration = canonical.LockDuration;
        ConsumeSettings.LockRenewal = canonical.LockRenewal;
        ConsumeSettings.LookbackInterval = canonical.LookbackInterval;
        ConsumeSettings.MaxDeliveryAttempts = canonical.MaxDeliveryAttempts;
        ConsumeSettings.BatchingWindow = canonical.BatchingWindow;
        ConsumeSettings.PerTenantTimeout = canonical.PerTenantTimeout;
        ConsumeSettings.PerTenantMaxDegreeOfParallelism = canonical.PerTenantMaxDegreeOfParallelism;
    }

    /// <inheritdoc/>
    public override string ToString() => ConsumerGroupId;
}
