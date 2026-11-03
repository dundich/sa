namespace Sa.Outbox.PostgreSql.Configuration;

/// <summary>
/// Bindable form of the provider-wide polling defaults (<see cref="PgPollingSettings"/>), read from
/// an <c>IConfiguration</c> section by
/// <see cref="PgOutboxPollingExtensions.WithPollingFromConfiguration"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every property is nullable: <see langword="null"/> means "not specified". <see cref="Profile"/>
/// and <see cref="Budget"/> are two alternative ways to fill the whole profile (the budget wins
/// when both are set); <see cref="BatchingWindow"/> is applied before them because the budget split
/// folds it in; the remaining explicit properties (<see cref="Interval"/>,
/// <see cref="IdleMaxInterval"/>, …) are applied last and therefore win over the profile and the
/// budget. <see cref="Enabled"/> = <see langword="false"/> turns the layer off regardless of the
/// other values.
/// </para>
/// <para>
/// A missing section is a no-op, so adding the section (or removing it) is a pure configuration
/// change — no redeploy of the polling values themselves.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// {
///   "Outbox": {
///     "Polling": {
///       "Budget": "00:01:00",
///       "BatchingWindow": "00:00:02"
///     }
///   }
/// }
/// </code>
/// </example>
public sealed class PgPollingOptions
{
    /// <summary>Enables or disables the layer. <see langword="false"/> ignores every other value.</summary>
    public bool? Enabled { get; set; }

    /// <summary>A named profile (<c>Quiet</c>, <c>Balanced</c>, <c>Responsive</c>).</summary>
    public PgPollingProfile? Profile { get; set; }

    /// <summary>
    /// The publication-to-processing budget for an idle group (for example <c>00:01:00</c>).
    /// Mutually exclusive in intent with <see cref="Profile"/> — when both are set, the budget wins.
    /// </summary>
    public TimeSpan? Budget { get; set; }

    /// <summary>Explicit poll period while the group has work.</summary>
    public TimeSpan? Interval { get; set; }

    /// <summary>Explicit upper bound of the empty-queue back-off (<c>00:00:00</c> = fixed interval).</summary>
    public TimeSpan? IdleMaxInterval { get; set; }

    /// <summary>Explicit delay before the group's first run after host start.</summary>
    public TimeSpan? InitialDelay { get; set; }

    /// <summary>Processing iterations per cycle (<c>-1</c> = drain until empty).</summary>
    public int? MaxProcessingIterations { get; set; }

    /// <summary>
    /// Explicit <see cref="Sa.Outbox.Delivery.OutboxConsumerSettings.BatchingWindow"/> override.
    /// Omit it unless intended: the window also delays the retry of an un-backoff-ed <c>Warn</c>.
    /// </summary>
    public TimeSpan? BatchingWindow { get; set; }

    /// <summary>Explicit per-tenant parallelism override (<c>1</c> = sequential, <c>-1</c> = one per CPU).</summary>
    public int? PerTenantMaxDegreeOfParallelism { get; set; }

    /// <summary>
    /// Applies the non-null values to <paramref name="settings"/>. Internal: the public entry point is
    /// the configuration-section extension, which binds this type and calls this method.
    /// </summary>
    internal void ApplyTo(PgPollingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (Enabled == false)
        {
            settings.Disable();
            return;
        }

        bool applied = false;

        // BatchingWindow first: UseBudget() folds it into the latency split, so a configured window
        // must be in place before the budget (or a profile) computes IdleMaxInterval.
        if (BatchingWindow is { } batchingWindow)
        {
            settings.WithBatchingWindow(batchingWindow);
            applied = true;
        }

        // A profile fills the whole surface; a budget recomputes it (budget wins over profile), and
        // the explicit values below win over both.
        if (Profile is { } profile) { settings.UseProfile(profile); applied = true; }
        if (Budget is { } budget) { settings.UseBudget(budget); applied = true; }

        if (Interval is { } interval) { settings.Interval = interval; applied = true; }
        if (IdleMaxInterval is { } idle) { settings.IdleMaxInterval = idle; applied = true; }
        if (InitialDelay is { } initialDelay) { settings.InitialDelay = initialDelay; applied = true; }
        if (MaxProcessingIterations is { } iterations) { settings.MaxProcessingIterations = iterations; applied = true; }

        if (PerTenantMaxDegreeOfParallelism is { } degree)
        {
            settings.WithPerTenantParallelism(degree);
            applied = true;
        }

        // Enabled = true with nothing else means "turn on with the core defaults"; any explicit
        // value already enabled the layer through its fluent method or the direct assignment.
        if (Enabled == true || applied)
            settings.Enabled = true;
    }
}
