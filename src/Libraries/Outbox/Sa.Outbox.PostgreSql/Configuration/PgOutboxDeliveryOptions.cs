using Microsoft.Extensions.Configuration;
using Sa.Outbox.Delivery;

namespace Sa.Outbox.PostgreSql.Configuration;

/// <summary>
/// Delivery-group settings bound from a single <c>IConfiguration</c> section
/// (<see cref="PgOutboxDeliveryOptionsProvider"/>). One instance per consumer group,
/// addressed by the <c>configurationKey</c> passed to
/// <c>AddDelivery(..., configurationKey: ...)</c> / <c>AddDeliveryScoped(..., configurationKey: ...)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every property is nullable: <c>null</> means "not specified", in which case
/// <see cref="ApplyTo(OutboxConsumerSettingsBuilder)"/> leaves that setting untouched so it
/// keeps whatever value the <see cref="OutboxConsumerSettingsBuilder"/> already carries
/// (<see cref="OutboxDefaults"/>, or an earlier override). Only non-null values are applied,
/// so a partially-filled section overrides only what it names.
/// </para>
/// <para>
/// Precedence (lowest → highest): <see cref="OutboxDefaults"/> → this section (only when a
/// <c>configurationKey</c> is supplied) → the user <c>configure</c> callback on
/// <c>AddDelivery</c>/<c>AddDeliveryScoped</c>. There is no implicit pre-seed layer in between.
/// </para>
/// <para>
/// <see cref="OutboxConsumerSettings.Paused"/> is deliberately excluded — it is runtime state,
/// controlled at runtime via <see cref="IOutboxConsumerManager"/>, not configuration.
/// </para>
/// <para>
/// <see cref="OutboxConsumerSettings.AsSingleton"/> is also excluded, and on purpose: it is the
/// lifetime contract of the <em>registration</em> (the difference between <c>AddDelivery</c> —
/// a keyed <em>singleton</em> consumer — and <c>AddDeliveryScoped</c> — a keyed <em>scoped</em>
/// consumer), not a per-group tunable. Overriding it here would decouple the runtime
/// <see cref="DeliveryLifetimeInvoker"/> path from the consumer's DI lifetime — e.g.
/// <c>AddDeliveryScoped</c> plus <c>AsSingleton: true</c> resolves a scoped consumer from the root
/// provider and throws. Keep it tied to the method you call.
/// </para>
/// </remarks>
public sealed class PgOutboxDeliveryOptions
{
    /// <summary>Interval between job executions. <see langword="null"/> — keep current.</summary>
    public TimeSpan? Interval { get; set; }

    /// <summary>Initial delay before the first run after startup. <see langword="null"/> — keep current.</summary>
    public TimeSpan? InitialDelay { get; set; }

    /// <summary>Group-level schedule concurrency limit. <see langword="null"/> — keep current.</summary>
    public int? ConcurrencyLimit { get; set; }

    /// <summary>Maximum number of simultaneously running jobs within this group. <see langword="null"/> — keep current.</summary>
    public int? MaxConcurrency { get; set; }

    /// <summary>Retries of the job on error (not of the message). <see langword="null"/> — keep current.</summary>
    public int? RetryCountOnError { get; set; }

    /// <summary>Upper bound for the rented batch size. <see langword="null"/> — keep current.</summary>
    public int? MaxBatchSize { get; set; }

    /// <summary>Maximum processing iterations per cycle (<c>-1</c> = drain until empty). <see langword="null"/> — keep current.</summary>
    public int? MaxProcessingIterations { get; set; }

    /// <summary>Delay between processing iterations within a cycle. <see langword="null"/> — keep current.</summary>
    public TimeSpan? IterationDelay { get; set; }

    /// <summary>Lock TTL for a rented task. <see langword="null"/> — keep current.</summary>
    public TimeSpan? LockDuration { get; set; }

    /// <summary>Lock renewal interval. <see langword="null"/> — keep current.</summary>
    public TimeSpan? LockRenewal { get; set; }

    /// <summary>How far back to scan for unprocessed messages. <see langword="null"/> — keep current.</summary>
    public TimeSpan? LookbackInterval { get; set; }

    /// <summary>Delivery-attempt budget before dead-lettering. <see langword="null"/> — keep current.</summary>
    public int? MaxDeliveryAttempts { get; set; }

    /// <summary>Window for accumulating messages into a batch. <see langword="null"/> — keep current.</summary>
    public TimeSpan? BatchingWindow { get; set; }

    /// <summary>Per-tenant processing timeout (zero = none). <see langword="null"/> — keep current.</summary>
    public TimeSpan? PerTenantTimeout { get; set; }

    /// <summary>Per-tenant parallelism (<c>1</c> = sequential, <c>-1</c> = one per CPU). <see langword="null"/> — keep current.</summary>
    public int? PerTenantMaxDegreeOfParallelism { get; set; }

    /// <summary>
    /// Upper bound for the adaptive empty-queue back-off (<see cref="TimeSpan.Zero"/> = disabled).
    /// <see langword="null"/> — keep current.
    /// </summary>
    public TimeSpan? IdleMaxInterval { get; set; }

    /// <summary>
    /// Applies every non-null property to <paramref name="builder"/>, leaving null properties
    /// untouched. Returns <see langword="true"/> when at least one property was applied.
    /// </summary>
    internal bool ApplyTo(OutboxConsumerSettingsBuilder builder)
    {
        bool applied = false;

        if (Interval is not null) { builder.WithInterval(Interval.Value); applied = true; }
        if (InitialDelay is not null) { builder.WithInitialDelay(InitialDelay.Value); applied = true; }
        if (ConcurrencyLimit is not null) { builder.WithConcurrencyLimit(ConcurrencyLimit.Value); applied = true; }
        if (MaxConcurrency is not null) { builder.WithMaxConcurrency(MaxConcurrency.Value); applied = true; }
        if (RetryCountOnError is not null) { builder.WithRetryCountOnError(RetryCountOnError.Value); applied = true; }
        if (MaxBatchSize is not null) { builder.WithMaxBatchSize(MaxBatchSize.Value); applied = true; }
        if (MaxProcessingIterations is not null) { builder.WithMaxProcessingIterations(MaxProcessingIterations.Value); applied = true; }
        if (IterationDelay is not null) { builder.WithIterationDelay(IterationDelay.Value); applied = true; }
        if (LockDuration is not null) { builder.WithLockDuration(LockDuration.Value); applied = true; }
        if (LockRenewal is not null) { builder.WithLockRenewal(LockRenewal.Value); applied = true; }
        if (LookbackInterval is not null) { builder.WithLookbackInterval(LookbackInterval.Value); applied = true; }
        if (MaxDeliveryAttempts is not null) { builder.WithMaxDeliveryAttempts(MaxDeliveryAttempts.Value); applied = true; }
        if (BatchingWindow is not null) { builder.WithBatchingWindow(BatchingWindow.Value); applied = true; }
        if (PerTenantTimeout is not null) { builder.WithPerTenantTimeout(PerTenantTimeout.Value); applied = true; }
        if (PerTenantMaxDegreeOfParallelism is not null) { builder.WithPerTenantMaxDegreeOfParallelism(PerTenantMaxDegreeOfParallelism.Value); applied = true; }

        // Zero means "adaptive back-off disabled" and maps to WithFixedInterval(); a positive
        // value enables it. WithIdleBackoff(Zero) throws, so the two must not be conflated.
        if (IdleMaxInterval is { } idle)
        {
            if (idle == TimeSpan.Zero) { builder.WithFixedInterval(); applied = true; }
            else { builder.WithIdleBackoff(idle); applied = true; }
        }

        return applied;
    }

    /// <summary>
    /// Binds a <see cref="PgOutboxDeliveryOptions"/> from the configuration section at
    /// <paramref name="configurationKey"/>. Returns <see langword="null"/> when the key is empty
    /// or the section is absent, so an unconfigured group keeps the builder's existing values.
    /// </summary>
    /// <remarks>
    /// Uses the instance <see cref="IConfigurationSection.Bind"/> (source-generated binder,
    /// AOT-safe) rather than <see cref="ConfigurationBinder.Bind{T}"/> (reflection) — the same
    /// choice the partitional PostgreSql settings make. A present-but-empty section binds every
    /// property to null, which <see cref="ApplyTo(OutboxConsumerSettingsBuilder)"/> treats as
    /// "apply nothing".
    /// </remarks>
    internal static PgOutboxDeliveryOptions? FromConfiguration(IConfiguration configuration, string configurationKey)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(configurationKey))
            return null;

        IConfigurationSection section = configuration.GetSection(configurationKey);
        if (!section.Exists())
            return null;

        PgOutboxDeliveryOptions options = new();
        section.Bind(options);
        return options;
    }
}
