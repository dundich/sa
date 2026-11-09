using Sa.Outbox.Delivery;

namespace Sa.Outbox.PostgreSql.Configuration;

/// <summary>
/// Named polling profiles: the trade-off between how often a consumer group polls an empty queue
/// and how long a freshly published message waits for its first delivery.
/// </summary>
/// <remarks>
/// Every profile keeps the worst case (publication to the start of processing) inside one minute
/// for an idle group: <c>BatchingWindow + IdleMaxInterval</c>, with the values below, is
/// 3 s + 45 s = 48 s at most.
/// </remarks>
public enum PgPollingProfile
{
    /// <summary>
    /// The quietest profile: the least polling while the queue is empty. Best for deployments with
    /// many consumer groups and tenants, where empty polls themselves are the load.
    /// </summary>
    Quiet = 0,

    /// <summary>
    /// The recommended default: reacts within half a minute while idle without polling the outbox
    /// more than a few times a minute.
    /// </summary>
    Balanced = 1,

    /// <summary>
    /// The most responsive profile: a few seconds while idle. Costs noticeably more empty polls, so
    /// prefer <see cref="Balanced"/> when the number of groups or tenants is large.
    /// </summary>
    Responsive = 2,
}

/// <summary>
/// Polling defaults applied to <em>every</em> consumer group of the PostgreSQL Outbox that does not
/// override them (a group's <c>IConfiguration</c> section and its <c>AddDelivery</c> callback both
/// win over these values).
/// </summary>
/// <remarks>
/// <para>
/// Registered by <c>WithPolling</c> / <c>WithPollingProfile</c> / <c>WithPollingBudget</c> on
/// <see cref="IPgOutboxConfiguration"/>. Without one of those calls no <see cref="PgPollingSettings"/>
/// is registered at all, so the provider's behaviour is exactly the same as before this type existed.
/// </para>
/// <para>
/// The point of the presets is the empty-queue trade-off documented in the core package: an idle
/// group is polled with <see cref="Interval"/> while work is flowing and backs off towards
/// <see cref="IdleMaxInterval"/> while the queue stays empty. Polling only (no notifications), so the
/// end-to-end latency of an idle group is bounded by
/// <c>BatchingWindow + IdleMaxInterval + drain time</c>.
/// </para>
/// <para>
/// This type deliberately does not touch <see cref="OutboxConsumerSettings.LockDuration"/>,
/// <see cref="OutboxConsumerSettings.LookbackInterval"/>, locks, batches or attempts: those are
/// per-group decisions with their own defaults.
/// </para>
/// </remarks>
public sealed class PgPollingSettings
{
    /// <summary>The reserve kept for draining a backlog, on top of the batch visibility window.</summary>
    private static readonly TimeSpan MinimumReserve = TimeSpan.FromSeconds(5);

    /// <summary>The smallest poll period a budget-derived profile will produce.</summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    /// <summary>The largest initial delay a profile will set (it only matters on host start).</summary>
    private static readonly TimeSpan MaximumInitialDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Enables this layer. Set to <see langword="true"/> by every preset/fluent method; a settings
    /// instance that stays <see langword="false"/> is ignored by the provider.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Poll period while the consumer group has work. Default is the core value (1 minute).</summary>
    public TimeSpan Interval { get; set; } = OutboxDefaults.Interval;

    /// <summary>
    /// Upper bound of the adaptive empty-queue back-off. <see cref="TimeSpan.Zero"/> keeps the poll
    /// period fixed at <see cref="Interval"/> (the core default).
    /// </summary>
    public TimeSpan IdleMaxInterval { get; set; } = OutboxDefaults.IdleMaxInterval;

    /// <summary>Delay before the group's first run after host start. Default is the core value (10 seconds).</summary>
    public TimeSpan InitialDelay { get; set; } = OutboxDefaults.InitialDelay;

    /// <summary>Processing iterations per cycle (<c>-1</c> = drain until empty).</summary>
    public int MaxProcessingIterations { get; set; } = OutboxDefaults.MaxProcessingIterations;

    /// <summary>
    /// When set, overrides the group's <see cref="OutboxConsumerSettings.BatchingWindow"/>.
    /// <see langword="null"/> (the default) leaves it alone — recommended, because the window is also
    /// the retry delay of an un-backoff-ed <c>Warn</c>.
    /// </summary>
    public TimeSpan? BatchingWindow { get; set; }

    /// <summary>
    /// When set, overrides the group's tenant parallelism. <see langword="null"/> (the default) leaves
    /// it alone.
    /// </summary>
    public int? PerTenantMaxDegreeOfParallelism { get; set; }

    /// <summary>
    /// Derives the whole profile from a delivery budget: the worst-case time from publishing a
    /// message to the start of its processing by an idle group.
    /// </summary>
    /// <remarks>
    /// The budget is split as <c>BatchingWindow (3 s) + reserve + IdleMaxInterval</c>, where the
    /// reserve is <c>max(5 s, 20% of the budget)</c> — room for draining a backlog, covering several
    /// tenants and absorbing the jitter of the back-off strategy. <see cref="Interval"/> is derived as
    /// a third of the idle bound, so a group with work still polls three times as often as an idle one.
    /// <see cref="MaxProcessingIterations"/> becomes <c>-1</c> so a burst is drained in the same cycle
    /// instead of waiting for the next tick.
    /// </remarks>
    /// <param name="deliveryBudget">The allowed publication-to-processing latency. Must exceed
    /// <see cref="OutboxDefaults.BatchingWindow"/> plus the minimum reserve.</param>
    /// <returns>The same instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The budget leaves no room for a poll period.</exception>
    public PgPollingSettings UseBudget(TimeSpan deliveryBudget)
    {
        TimeSpan batchingWindow = BatchingWindow ?? OutboxDefaults.BatchingWindow;

        TimeSpan reserve = TimeSpan.FromTicks(Math.Max(
            MinimumReserve.Ticks,
            (long)(deliveryBudget.Ticks * 0.2)));

        long idleTicks = deliveryBudget.Ticks - batchingWindow.Ticks - reserve.Ticks;

        if (deliveryBudget <= TimeSpan.Zero || idleTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deliveryBudget),
                deliveryBudget,
                $"The delivery budget must exceed the batching window ({batchingWindow}) plus a reserve of at least {MinimumReserve}.");
        }

        IdleMaxInterval = TimeSpan.FromTicks(idleTicks);
        Interval = TimeSpan.FromTicks(Math.Max(MinimumInterval.Ticks, idleTicks / 3));
        InitialDelay = TimeSpan.FromTicks(Math.Min(MaximumInitialDelay.Ticks, Interval.Ticks));
        MaxProcessingIterations = -1;
        Enabled = true;

        return this;
    }

    /// <summary>
    /// Applies one of the named profiles. See <see cref="PgPollingProfile"/> for the values and for
    /// the worst-case latency each one guarantees.
    /// </summary>
    /// <param name="profile">The profile to apply.</param>
    /// <returns>The same instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined profile.</exception>
    public PgPollingSettings UseProfile(PgPollingProfile profile = PgPollingProfile.Balanced)
    {
        (Interval, IdleMaxInterval, InitialDelay) = profile switch
        {
            PgPollingProfile.Quiet =>
                (TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(5)),
            PgPollingProfile.Balanced =>
                (TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)),
            PgPollingProfile.Responsive =>
                (TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(3)),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown polling profile."),
        };

        MaxProcessingIterations = -1;
        Enabled = true;

        return this;
    }

    /// <summary>
    /// Overrides the group's <see cref="OutboxConsumerSettings.BatchingWindow"/>.
    /// </summary>
    /// <remarks>
    /// Set this only deliberately: the window is also what makes an un-backoff-ed <c>Warn</c> wait
    /// before its retry, so lowering it changes retry timing as well as first-delivery latency.
    /// </remarks>
    /// <param name="batchingWindow">The window; <see cref="TimeSpan.Zero"/> disables aggregation.</param>
    /// <returns>The same instance for chaining.</returns>
    public PgPollingSettings WithBatchingWindow(TimeSpan batchingWindow)
    {
        if (batchingWindow.Ticks < 0)
            throw new ArgumentOutOfRangeException(nameof(batchingWindow), batchingWindow, "BatchingWindow must be >= TimeSpan.Zero.");

        BatchingWindow = batchingWindow;
        Enabled = true;
        return this;
    }

    /// <summary>
    /// Overrides the group's tenant parallelism — useful when many tenants are served by one group
    /// and a slow consumer on one tenant must not delay the rest.
    /// </summary>
    /// <param name="degree"><c>1</c> = sequential, <c>&gt; 1</c> = parallel, <c>-1</c> = one per CPU.</param>
    /// <returns>The same instance for chaining.</returns>
    public PgPollingSettings WithPerTenantParallelism(int degree)
    {
        if (degree == 0 || degree < -1)
            throw new ArgumentOutOfRangeException(
                nameof(degree),
                degree,
                "PerTenantMaxDegreeOfParallelism must be -1 (all processors) or greater than 0.");

        PerTenantMaxDegreeOfParallelism = degree;
        Enabled = true;
        return this;
    }

    /// <summary>Turns the layer off again (the provider then ignores it).</summary>
    /// <returns>The same instance for chaining.</returns>
    public PgPollingSettings Disable()
    {
        Enabled = false;
        return this;
    }

    /// <summary>
    /// Rejects values that <see cref="OutboxConsumerSettingsBuilder"/> alone would accept but the
    /// final settings would not: a back-off bound below the base interval, a non-positive interval,
    /// a negative initial delay or an impossible iteration budget.
    /// </summary>
    internal void Validate()
    {
        if (!Enabled) return;

        if (Interval <= TimeSpan.Zero)
            throw new InvalidOperationException($"PgPollingSettings.Interval must be > TimeSpan.Zero, got {Interval}.");

        if (IdleMaxInterval < TimeSpan.Zero)
            throw new InvalidOperationException($"PgPollingSettings.IdleMaxInterval must be >= TimeSpan.Zero, got {IdleMaxInterval}.");

        if (IdleMaxInterval > TimeSpan.Zero && IdleMaxInterval < Interval)
            throw new InvalidOperationException(
                $"PgPollingSettings.IdleMaxInterval ({IdleMaxInterval}) must be greater than or equal to Interval ({Interval}).");

        if (InitialDelay < TimeSpan.Zero)
            throw new InvalidOperationException($"PgPollingSettings.InitialDelay must be >= TimeSpan.Zero, got {InitialDelay}.");

        if (MaxProcessingIterations < -1)
            throw new InvalidOperationException(
                $"PgPollingSettings.MaxProcessingIterations must be >= -1, got {MaxProcessingIterations}.");

        if (BatchingWindow is { } batchingWindow && batchingWindow < TimeSpan.Zero)
            throw new InvalidOperationException($"PgPollingSettings.BatchingWindow must be >= TimeSpan.Zero, got {batchingWindow}.");

        if (PerTenantMaxDegreeOfParallelism is { } degree && (degree == 0 || degree < -1))
            throw new InvalidOperationException(
                $"PgPollingSettings.PerTenantMaxDegreeOfParallelism must be -1 or greater than 0, got {degree}.");
    }

    /// <summary>
    /// Applies the profile to a group's settings builder. Returns <see langword="false"/> when the
    /// layer is disabled, so the caller can report "nothing applied".
    /// </summary>
    internal bool ApplyTo(OutboxConsumerSettingsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!Enabled) return false;

        builder
            .WithInterval(Interval)
            .WithInitialDelay(InitialDelay)
            .WithMaxProcessingIterations(MaxProcessingIterations);

        // Zero means "no back-off" and maps to WithFixedInterval(); WithIdleBackoff rejects it.
        if (IdleMaxInterval > TimeSpan.Zero)
            builder.WithIdleBackoff(IdleMaxInterval);
        else
            builder.WithFixedInterval();

        if (BatchingWindow is { } batchingWindow)
            builder.WithBatchingWindow(batchingWindow);

        if (PerTenantMaxDegreeOfParallelism is { } degree)
            builder.WithPerTenantMaxDegreeOfParallelism(degree);

        return true;
    }

    /// <inheritdoc/>
    public override string ToString()
        => Enabled
            ? $"polling interval={Interval}, idleMax={IdleMaxInterval}, initialDelay={InitialDelay}, " +
              $"iterations={MaxProcessingIterations}, batchingWindow={(BatchingWindow?.ToString() ?? "default")}"
            : "polling disabled";
}
